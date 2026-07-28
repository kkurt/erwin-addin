#nullable enable
using System;
using System.Collections.Generic;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// DIAGNOSTIC ONLY (2026-07-28). Asks the live model whether it exposes the diagram
    /// selection as a plain metamodel property.
    ///
    /// <para><b>Why this is worth 15 lines.</b> "SCAPI has no selection API" is true of every
    /// documented INTERFACE - a full enumeration of the type library in EAL.dll (71 typeinfos)
    /// has no Selection member anywhere. But erwin resolves property names dynamically against
    /// the live metamodel, and the metamodel data files DO carry a selection property class:
    /// <c>EMXPropertyNames.data</c> holds id 19339 "Current Selection",
    /// <c>EMXLPropertyAssociations.data</c> holds <c>Model__has__CurrentSelection</c> on owner
    /// class 2 (Model), sitting between <c>Model__has__CurrentERDiagramRef</c> and
    /// <c>Model__has__CurrentZoomLevel</c>, and <c>EM_EMX.dll</c> exports
    /// <c>?pCurrentSelection@EMXTypes@@3VGDMId@@A</c>.</para>
    ///
    /// <para>The obvious counter-argument - that <c>Current_Selection</c> does not appear in
    /// SCAPI.dll's string table - was control-tested and does not hold: <c>Model_Object_Ref</c>
    /// is equally absent from that table and is read successfully in production every day by
    /// <c>TableTypeMonitorService</c>. So the only way to know is to ask the running model.</para>
    ///
    /// <para>If this returns a live value the whole problem is solved in pure COM, with no
    /// native bridge, no undocumented export and no mangled name to break on the next erwin
    /// build. If it comes back empty, the sibling properties read here say WHY: a dormant
    /// <c>Current_Tool</c> and <c>Current_Zoom_Level</c> alongside a working
    /// <c>Current_ER_Diagram_Ref</c> means this family is persisted-but-not-live, and the
    /// investigation moves to the native <c>ERD::GetSelectedModelObjectIds</c> route.</para>
    ///
    /// <para>Read-only, DEV builds only, never throws. Delete once the question is settled.</para>
    /// </summary>
    internal static class SelectionProbe
    {
        public const string LogPrefix = "[SEL-PROBE]";

        // Current_Selection is the target. The rest are CONTROLS, and each earns its place:
        //  - Current_ER_Diagram_Ref is known-live (read in production), so an empty result from
        //    it means the probe itself is wrong, not that the property is dormant.
        //  - Current_Tool / Current_Zoom_Level are its immediate neighbours in the metamodel
        //    association table; if the whole neighbourhood reads empty, the family is dormant.
        private static readonly string[] Candidates =
        {
            "Current_Selection",
            "Current_ER_Diagram_Ref",
            "Current_Tool",
            "Current_Zoom_Level",
            "Selected_Objects",
            "Selection",
        };

        private static bool _ran;

        /// <summary>
        /// Reads each candidate property off the model root and logs the raw type and value.
        /// Once per process: the answer does not change within a session.
        /// </summary>
        /// <param name="modelRoot">The model root object (boxed; cast to dynamic inside).</param>
        public static void RunOnce(object? modelObjects, object? modelRoot, Action<string>? log)
        {
            void Write(string message)
            {
                try { log?.Invoke($"{LogPrefix} {message}"); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"{LogPrefix} log sink threw: {ex.Message}"); }
            }

            if (modelRoot == null) return;
            if (_ran) return;
            _ran = true;

            Write("reading selection-shaped properties off the model root " +
                  "(select 2+ entities on the diagram before triggering this to make the result meaningful)");

            Write("on MODEL root:");
            ReadCandidates(modelRoot, Write);

            // Round 2, and the reason this probe was worth extending rather than abandoning.
            // On the model root erwin answered Current_Selection with "Property Current_Selection
            // class cannot be assigned to object of Model class" - a DIFFERENT error from the
            // "not a valid class id, class name or object property" it gave for the invented
            // names Selection / Selected_Objects. So the property CLASS exists and erwin knows
            // the name; it is simply not carried by the Model class. A selection is a property of
            // a DIAGRAM, so ask the diagrams.
            if (modelObjects == null) return;
            try
            {
                dynamic mo = modelObjects;
                dynamic diagrams = mo.Collect(modelRoot, "ER_Diagram", -1);
                if (diagrams == null) { Write("no ER_Diagram objects collected."); return; }

                int index = 0;
                foreach (dynamic diagram in diagrams)
                {
                    if (diagram == null) continue;
                    object diagramRef = diagram;

                    string id = "";
                    string name = "";
                    try { id = diagram.ObjectId?.ToString() ?? ""; } catch { /* logged via the read below */ }
                    try { name = diagram.Name?.ToString() ?? ""; } catch { /* ditto */ }

                    Write($"on ER_Diagram[{index++}] '{name}' {id}:");
                    ReadCandidates(diagramRef, Write);

                    // Control on THIS class: the metamodel documents Selection_Grips_Color on
                    // ER_Diagram, so a successful read proves we are addressing the right object
                    // and that a failure above is about the property, not the target.
                    ReadOne(diagramRef, "Selection_Grips_Color", Write);

                    if (index >= 3) { Write("(stopping after 3 diagrams)"); break; }
                }
            }
            catch (Exception ex)
            {
                Write($"ER_Diagram walk failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void ReadCandidates(object? target, Action<string> write)
        {
            if (target == null) return;
            foreach (string name in Candidates) ReadOne(target, name, write);
        }

        private static void ReadOne(object target, string name, Action<string> write)
        {
            try
            {
                dynamic obj = target;
                object? raw = obj.Properties(name).Value;
                if (raw == null)
                {
                    write($"  {name,-24} = <null>");
                    return;
                }

                write($"  {name,-24} = [{raw.GetType().Name}] '{Describe(raw)}'");
            }
            catch (Exception ex)
            {
                // Which properties FAIL is half the answer, and HOW they fail is the other half:
                // "cannot be assigned to object of X class" means the property class exists but
                // is not carried here, whereas "not a valid class id, class name or object
                // property" means the name does not exist at all. Logged verbatim, never
                // swallowed.
                write($"  {name,-24} ! {ex.Message}");
            }
        }

        /// <summary>
        /// Renders a property value without assuming its shape. A selection would plausibly come
        /// back as an array of ids or a delimited string, so arrays are expanded rather than
        /// printed as "System.Object[]".
        /// </summary>
        private static string Describe(object raw)
        {
            if (raw is string s) return s;

            if (raw is System.Collections.IEnumerable seq)
            {
                var parts = new List<string>();
                foreach (var item in seq)
                {
                    parts.Add(item?.ToString() ?? "<null>");
                    if (parts.Count >= 50) { parts.Add("..."); break; }
                }
                return $"{parts.Count} item(s): " + string.Join(", ", parts);
            }

            return raw.ToString() ?? "";
        }
    }
}
