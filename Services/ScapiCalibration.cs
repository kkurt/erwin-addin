#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// DIAGNOSTIC ONLY (2026-07-27). Times the individual SCAPI access shapes a model walk is
    /// built from, so a slow walk can be attributed to a shape instead of guessed at.
    ///
    /// <para><b>Why this exists.</b> The approval blocking-rule walk was measured at 467 s over
    /// 8,401 columns while <c>ValidationCoordinatorService.BaselineDiagramHeartbeat</c> walks
    /// the SAME model with the SAME shapes - <c>Collect(root,"Entity")</c>, a
    /// <c>Collect(entity,"Attribute")</c> per entity, 8,400 <c>ObjectId</c> reads and 286
    /// <c>Properties("Physical_Name")</c> reads - in <b>734 ms</b>. The gate's collect phase
    /// does strictly less COM work than that and still takes 179 s. Two attributions have
    /// already been wrong about magnitude, so the next step is a measurement, not a third
    /// theory.</para>
    ///
    /// <para>Run from BOTH contexts in one session (connect, and inside the modal review
    /// dialog's message loop) the output separates the two surviving explanations:
    /// <list type="bullet">
    /// <item><description>if the per-shape costs are the same in both contexts, the shape is
    /// the problem - a named <c>Properties(name)</c> lookup over the model's ~1,500
    /// Property_Types, which the walk does per object;</description></item>
    /// <item><description>if the SAME shape is orders of magnitude slower inside the modal
    /// loop, the context is the problem - every SCAPI call pumps that loop, and erwin's own
    /// frame is disabled by <see cref="ErwinInputBlock"/> while it runs.</description></item>
    /// </list></para>
    ///
    /// <para>Compiled into DEV builds only. Delete it, or its call sites, once the walk's cost
    /// is attributed - it is a probe, not a feature.</para>
    /// </summary>
    internal static class ScapiCalibration
    {
        public const string LogPrefix = "[SCAPI-CAL]";

        // Small enough that the worst observed per-op cost (~34 ms) still finishes a shape
        // inside the budget below, large enough that one slow outlier cannot dominate.
        private const int SampleSize = 150;

        // Hard per-shape ceiling. Without it, a shape that turns out to cost 300 ms/op would
        // add 45 s to a connect or a submit just to measure itself.
        private const int PerShapeBudgetMs = 4000;

        // Once per context per process: the answer does not change within a session, and this
        // probe is not free.
        private static readonly HashSet<string> Done = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Re-run the probe for a context that has already reported. Used to prove a fix moved
        /// the cost rather than moved the measurement: the same context measured twice in one
        /// session must agree.
        /// </summary>
        public static void Reset() { lock (Done) Done.Clear(); }

        /// <summary>
        /// Times each shape once for <paramref name="context"/> and writes the result through
        /// <paramref name="write"/>. Never throws: a probe must not be able to break the walk
        /// it is measuring.
        /// </summary>
        /// <param name="modelObjects">Session ModelObjects (boxed; cast to dynamic inside).</param>
        /// <param name="root">Model root (boxed).</param>
        /// <param name="context">Where this ran, e.g. "connect (no modal)" - appears in every line.</param>
        public static void RunOnce(object? modelObjects, object? root, string context, Action<string>? write)
        {
            void Write(string message)
            {
                try { write?.Invoke($"{LogPrefix} {message}"); }
                catch (Exception ex) { Debug.WriteLine($"{LogPrefix} log sink threw: {ex.Message}"); }
            }

            if (modelObjects == null || root == null) return;
            lock (Done)
            {
                if (!Done.Add(context)) return;
            }

            try
            {
                Measure(modelObjects, root, context, Write);
            }
            catch (Exception ex)
            {
                Write($"context={context} ABORTED - {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Measure(object modelObjects, object root, string context, Action<string> write)
        {
            // The one fact that discriminates every remaining explanation. A call from an STA
            // thread to a COM object in the SAME apartment is a direct dispatch and cannot cost
            // milliseconds; a CROSS-apartment call is marshalled, pumps, and is subject to
            // erwin's OLE message filter (which can retry-with-delay while the app is "busy" -
            // exactly what a modal dialog makes it). So if these two lines differ between the
            // connect block and the gate block, the walk is simply running on the wrong thread.
            var thread = System.Threading.Thread.CurrentThread;
            write($"context={context} thread=#{Environment.CurrentManagedThreadId} " +
                  $"apartment={thread.GetApartmentState()} name='{thread.Name ?? "(unnamed)"}' " +
                  $"isThreadPool={thread.IsThreadPoolThread}");

            dynamic mo = modelObjects;
            dynamic rt = root;

            var clock = Stopwatch.StartNew();
            dynamic entities = mo.Collect(rt, "Entity");
            clock.Stop();
            double rootCollectMs = clock.Elapsed.TotalMilliseconds;
            if (entities == null)
            {
                write($"context={context} - Collect(root,\"Entity\") returned null; nothing to measure.");
                return;
            }

            // Shape 1+2: the per-entity Collect and the attribute enumeration, timed apart so a
            // slow Collect cannot be mistaken for a slow enumerator (or the reverse).
            var sample = new List<object>(SampleSize);
            double entityCollectMs = 0, enumerateMs = 0;
            int entityCollects = 0, enumerated = 0;
            var budget = Stopwatch.StartNew();

            foreach (dynamic entity in entities)
            {
                if (sample.Count >= SampleSize || budget.ElapsedMilliseconds > PerShapeBudgetMs) break;
                if (entity == null) continue;

                var collectClock = Stopwatch.StartNew();
                dynamic attrs = mo.Collect(entity, "Attribute");
                collectClock.Stop();
                entityCollectMs += collectClock.Elapsed.TotalMilliseconds;
                entityCollects++;
                if (attrs == null) continue;

                var enumClock = Stopwatch.StartNew();
                foreach (dynamic attr in attrs)
                {
                    if (attr == null) continue;
                    sample.Add((object)attr);
                    enumerated++;
                    if (sample.Count >= SampleSize) break;
                }
                enumClock.Stop();
                enumerateMs += enumClock.Elapsed.TotalMilliseconds;
            }

            write($"context={context} sample={sample.Count} object(s) from {entityCollects} table(s)");
            write($"  Collect(root,\"Entity\")            {rootCollectMs,10:F1} ms");
            write($"  Collect(entity,\"Attribute\")       {entityCollectMs,10:F1} ms over {entityCollects} call(s) = {Per(entityCollectMs, entityCollects)} ms/call");
            write($"  enumerate attributes              {enumerateMs,10:F1} ms over {enumerated} step(s) = {Per(enumerateMs, enumerated)} ms/step");

            // Shape 3: reads over the SAME objects, so nothing but the access shape differs.
            // .ObjectId is a direct member; Properties(name) is a named lookup over the model's
            // Property_Types. That difference is one of the two surviving explanations.
            Report(write, "read .ObjectId (direct member)", sample, o => { dynamic d = o; _ = d.ObjectId?.ToString(); });
            Report(write, "read Properties(\"Physical_Data_Type\")", sample, o => { dynamic d = o; _ = d.Properties("Physical_Data_Type").Value?.ToString(); });
            Report(write, "read Properties(\"Physical_Name\")", sample, o => { dynamic d = o; _ = d.Properties("Physical_Name").Value?.ToString(); });
        }

        private static void Report(Action<string> write, string label, List<object> sample, Action<object> op)
        {
            var clock = Stopwatch.StartNew();
            int done = 0, threw = 0;
            foreach (var target in sample)
            {
                if (clock.ElapsedMilliseconds > PerShapeBudgetMs) break;
                // A throw is not swallowed here, it is COUNTED and reported: "every read threw"
                // would itself be the answer (a caught COM exception costs milliseconds), and
                // that is exactly the kind of cost the walk's own silent catches were hiding.
                try { op(target); }
                catch { threw++; }
                done++;
            }
            clock.Stop();
            double ms = clock.Elapsed.TotalMilliseconds;
            write($"  {label,-38}{ms,10:F1} ms over {done} read(s), {threw} threw = {Per(ms, done)} ms/read");
        }

        private static string Per(double totalMs, int count)
            => count <= 0 ? "n/a" : (totalMs / count).ToString("F3");
    }
}
