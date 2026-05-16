#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Empirical SCAPI property discovery for naming-standard authoring.
    /// erwin r10.10 does not ship a public property catalog that lists which
    /// <c>Properties("XYZ").Value</c> accessor names are valid on which
    /// object class for which DBMS. Admin's <c>MC_PROPERTY_DEF.PROPERTY_CODE</c>
    /// has to match the SCAPI accessor name exactly or
    /// <c>scapiObject.Properties(code).Value</c> throws "is not valid class
    /// id or class name for object or property" (bug observed 2026-05-16
    /// with admin's <c>'Owner'</c> code).
    /// <para>
    /// Approach (revised 2026-05-16 after first probe attempt):
    /// </para>
    /// <para>
    /// The first cut tried <c>foreach</c> over <c>obj.Properties</c> and
    /// read <c>p.Name</c> on each <see cref="System.Object"/>. The iteration
    /// worked but the per-item <c>Name</c> accessor returned
    /// <c>&lt;&lt;not-readable&gt;&gt;</c> for every property - ISCModelProperty
    /// on r10.10 does not expose its own name; the name lives on the
    /// owning collection (<c>obj.Properties("Foo")</c>) only. So the
    /// iteration produced 64 values with no labels.
    /// </para>
    /// <para>
    /// The current approach instead hits a curated candidate list per
    /// class. For each candidate accessor name we try
    /// <c>obj.Properties(name).Value</c>; if SCAPI accepts the name we log
    /// it, otherwise we move on. Output is grep-friendly:
    /// <c>[PHYS] Schema_Name = 'dbo'</c>. Admins run the probe once per
    /// DBMS and read the working accessor names off the dump.
    /// </para>
    /// </summary>
    public class MetamodelPropertyProbeService
    {
        // ----- Candidate lists per class. Drawn from -----
        //  1. erwin metamodel association data
        //     (Program Files\erwin\Data Modeler r10\EMXLPropertyAssociations.data)
        //     Association names ending in __<Suffix> -> SCAPI accessor "Suffix"
        //     (sometimes with underscores between words, e.g. PhysicalName ->
        //     Physical_Name). Both spellings are tried.
        //  2. ErwinAlterDdl/ActiveSessionMapProvider.ReadSchemaProperty fallback chain.
        //  3. erwin SCAPI Reference Guide 15 examples.
        // Add candidates here over time as new DBMS variants show up.

        private static readonly string[] CommonCandidates =
        {
            "Physical_Name", "PhysicalName", "Name",
            "Definition", "Comment",
            "Note", "Note2", "Note3", "NoteList",
            "Author", "Description", "Version",
            "IsLogicalOnly", "IsPhysicalOnly",
            "Hide_In_Logical", "Hide_In_Physical",
            "UserFormattedName", "UserFormattedPhysicalName",
        };

        private static readonly string[] EntityCandidates =
        {
            // Universal schema/owner family
            "Schema_Name", "SchemaName", "Schema", "SchemaRef",
            "Owner", "Owner_Name", "OwnerName",
            "Owner_Schema", "Owner_Schema_Name", "OwnerSchemaName",
            "Name_Qualifier", "NameQualifier",
            // SQL Server specific
            "SQL_Server_Schema_Name", "SQLServerSchemaName",
            "SQL_Server_Schema", "SQLServerSchema",
            // Oracle / Db2 historic candidates
            "Oracle_Owner", "Db2_Schema", "Db2zos_Schema", "Table_Owner",
            // Storage / placement
            "Tablespace_Ref", "Tablespace_Name", "Filegroup",
            "Archive_Name", "DB2_Database_Ref",
            // Entity-class extras
            "Entity_Type", "Is_Physical_Only", "Is_Logical_Only",
            "User_Defined_SQL", "If_Not_Exists",
            "Remote_Table_Schema", "Database_Id", "Database_Internal_Name",
        };

        private static readonly string[] AttributeCandidates =
        {
            // Datatype
            "Physical_Data_Type", "PhysicalDataType", "Logical_Data_Type",
            "Domain", "Domain_Ref", "Domain_Parent_Ref",
            // Nullability / default / identity
            "Null_Option_Type", "NullOption", "Default_Value", "DefaultValue",
            "Identity_Start", "Identity_Increment", "Is_Identity",
            // Constraints / role
            "Is_PK", "Is_FK", "Is_AK", "Primary_Key", "Foreign_Key",
            // Owning entity (for completeness)
            "Owner_Entity", "Owning_Entity_Name", "Owning_Entity_Ref",
            // Column-level note / comment alternatives
            "PB_Comment", "Data_Source_Comment",
        };

        private static readonly string[] ViewCandidates =
        {
            "Schema_Name", "SchemaName", "Schema", "Owner",
            "View_SQL", "ViewSQL", "Materialized",
            "SQL_Server_Use_Schema_Binding",
        };

        private static readonly string[] SequenceCandidates =
        {
            "Schema_Name", "Owner", "Owner_Schema",
            "Sequence_Start", "Sequence_Increment",
            "Sequence_Cache_Size", "Sequence_Min_Value", "Sequence_Max_Value",
            "Cycle", "Order_Flag",
        };

        private static readonly string[] KeyGroupCandidates =
        {
            "Key_Group_Type", "KeyGroupType", "Index_Type",
            "Tablespace_Ref", "Tablespace_Name", "Filegroup",
            "Is_Clustered", "Is_Unique", "Where_Clause", "Cluster",
            "Include_Columns",
        };

        private static readonly string[] SubjectAreaCandidates =
        {
            "Color", "Background_Color",
        };

        private static readonly string[] ModelCandidates =
        {
            "Target_Server", "TargetServer", "Database_Type", "DBMS_Type",
            "Default_Schema", "Logical_Naming_Conventions",
        };

        // Heuristic classification of property names. Pure name-based.
        private static readonly string[] LogicalHints =
        {
            "Logical", "Generalization", "Subtype", "Inheritance",
        };

        private static readonly string[] UiHints =
        {
            "Color", "Font", "Diagram", "Position", "Width", "Height",
            "Display", "Drawing", "Z_Order", "ZOrder", "ScaledPage",
            "Margin", "Layout", "Hide_",
        };

        // ----- Class probe configuration -----

        private record ProbeTarget(string ClassKey, string Label, string[] Extras);

        private static readonly ProbeTarget[] ProbeTargets =
        {
            new("Entity",       "Table (Entity)",         EntityCandidates),
            new("Attribute",    "Column (Attribute)",     AttributeCandidates),
            new("View",         "View",                   ViewCandidates),
            new("Sequence",     "Sequence",               SequenceCandidates),
            new("Key_Group",    "Index / Key Group",      KeyGroupCandidates),
            new("Subject_Area", "Subject Area",           SubjectAreaCandidates),
        };

        private readonly dynamic _session;
        private readonly dynamic _scapi;
        private readonly dynamic _currentModel;

        public event Action<string>? OnLog;

        public MetamodelPropertyProbeService(dynamic session, dynamic scapi, dynamic currentModel)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _scapi = scapi ?? throw new ArgumentNullException(nameof(scapi));
            _currentModel = currentModel ?? throw new ArgumentNullException(nameof(currentModel));
        }

        /// <summary>
        /// Run the full probe and return the path of the dump file. Output
        /// is written under <see cref="Path.GetTempPath"/>.
        /// </summary>
        public string RunProbe()
        {
            string outPath = Path.Combine(Path.GetTempPath(),
                $"erwin-property-probe-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var sb = new StringBuilder();

            WriteHeader(sb, outPath);

            // Root model first (always exists).
            DumpRoot(sb);
            sb.AppendLine();

            int totalFound = 0;
            int classesWithInstances = 0;
            foreach (var target in ProbeTargets)
            {
                int found = DumpFirstInstance(sb, target);
                if (found > 0)
                {
                    totalFound += found;
                    classesWithInstances++;
                }
                sb.AppendLine();
            }

            if (classesWithInstances == 0)
            {
                sb.AppendLine("---");
                sb.AppendLine("NOTE: this model has zero Entity / Attribute / View / Sequence / Key_Group / Subject_Area");
                sb.AppendLine("instances. The probe cannot read property accessors on classes with no instances.");
                sb.AppendLine();
                sb.AppendLine("To produce a useful dump, add at least one of each interesting class to the model:");
                sb.AppendLine("  - drop a Table on the diagram");
                sb.AppendLine("  - give it a couple of Columns");
                sb.AppendLine("  - add an Index (Primary or Inversion Entry) on one column");
                sb.AppendLine("  - add a View (Database Object Properties > View)");
                sb.AppendLine("  - add a Subject Area");
                sb.AppendLine("Then re-run the probe.");
            }

            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            Log($"MetamodelPropertyProbe: wrote {sb.Length} chars to {outPath} (classes with instances: {classesWithInstances})");
            return outPath;
        }

        // ----- Output building -----

        private void WriteHeader(StringBuilder sb, string outPath)
        {
            sb.AppendLine($"=== MetamodelPropertyProbe @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"Log file: {outPath}");
            sb.AppendLine();

            // Active context (so admin knows which DBMS this dump belongs to).
            var ctx = ConfigContextService.Instance;
            sb.AppendLine("Active context:");
            sb.AppendLine($"  Mart path:       {ctx.MartPath ?? "(none)"}");
            sb.AppendLine($"  CONFIG name:     {ctx.ActiveConfigName ?? "(none)"} (ID={ctx.ActiveConfigId})");
            sb.AppendLine($"  DBMS_VERSION_ID: {ctx.DbmsVersionId?.ToString() ?? "(unknown)"}");

            // Try to read erwin's own model-level Target_Server and Name.
            string modelName = TryReadName(_currentModel);
            string targetServer = TryReadAnyProperty(_currentModel, new[]
            {
                "Target_Server", "TargetServer", "Database_Type", "DBMS_Type",
            });
            string modelLocator = TryReadAnyProperty(_currentModel, new[] { "Locator", "Name" });
            sb.AppendLine($"  Model name:      {modelName}");
            sb.AppendLine($"  Target server:   {targetServer}");
            sb.AppendLine($"  PU locator hint: {modelLocator}");
            sb.AppendLine();

            sb.AppendLine("Legend: [PHYS] physical/business prop (likely subject to standards)");
            sb.AppendLine("        [LOG]  logical-only (skip for physical naming rules)");
            sb.AppendLine("        [UI]   diagram / drawing / cosmetic");
            sb.AppendLine();
            sb.AppendLine("Approach: candidate accessor names are tried one-by-one; only those");
            sb.AppendLine("SCAPI accepts get printed. Use the printed names as PROPERTY_CODE in");
            sb.AppendLine("MC_PROPERTY_DEF.");
            sb.AppendLine();
        }

        private void DumpRoot(StringBuilder sb)
        {
            dynamic? root = TryGet(() => _session.ModelObjects.Root);
            sb.AppendLine("[Model (root)]");
            if (root == null)
            {
                sb.AppendLine("  root not accessible.");
                return;
            }
            int dumped = ProbeCandidates(sb, root, CommonCandidates, ModelCandidates);
            sb.AppendLine($"  -- {dumped} property accessors confirmed");
        }

        private int DumpFirstInstance(StringBuilder sb, ProbeTarget target)
        {
            dynamic objects = _session.ModelObjects;
            dynamic root = objects.Root;

            dynamic? collection;
            try { collection = objects.Collect(root, target.ClassKey); }
            catch (Exception ex)
            {
                sb.AppendLine($"[{target.Label}] Collect('{target.ClassKey}') failed: {ex.Message}");
                return 0;
            }

            if (collection == null)
            {
                sb.AppendLine($"[{target.Label}] Collect returned null");
                return 0;
            }

            dynamic? first = null;
            int total = 0;
            try
            {
                foreach (dynamic item in collection)
                {
                    if (item == null) continue;
                    total++;
                    if (first == null) first = item;
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[{target.Label}] iteration failed: {ex.Message}");
                return 0;
            }

            if (first == null)
            {
                sb.AppendLine($"[{target.Label}] no instances in this model");
                return 0;
            }

            string nameHint = TryReadName(first);
            sb.AppendLine($"[{target.Label} ('{nameHint}', 1 of {total})]");
            int dumped = ProbeCandidates(sb, first, CommonCandidates, target.Extras);
            sb.AppendLine($"  -- {dumped} property accessors confirmed");
            return dumped;
        }

        /// <summary>
        /// Walk both candidate lists, attempt <c>obj.Properties(name).Value</c>
        /// for each, and log accepted ones. Returns the number of accessors
        /// that worked.
        /// </summary>
        private static int ProbeCandidates(StringBuilder sb, dynamic obj, string[] common, string[] extras)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int dumped = 0;

            void TryOne(string name)
            {
                if (!seen.Add(name)) return;
                if (!TryReadValue(obj, name, out string value)) return;
                sb.AppendLine($"  {Classify(name)} {name} = '{Truncate(value, 80)}'");
                dumped++;
            }

            foreach (var n in common) TryOne(n);
            foreach (var n in extras) TryOne(n);
            return dumped;
        }

        // ----- Helpers -----

        private static string Classify(string name)
        {
            if (ContainsAny(name, LogicalHints)) return "[LOG] ";
            if (ContainsAny(name, UiHints)) return "[UI]  ";
            return "[PHYS]";
        }

        private static bool ContainsAny(string s, string[] needles)
        {
            foreach (var n in needles)
                if (s.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static string TryReadName(dynamic obj)
        {
            if (TryReadValue(obj, "Physical_Name", out string pn) && !string.IsNullOrEmpty(pn)) return pn;
            if (TryReadValue(obj, "Name", out string n) && !string.IsNullOrEmpty(n)) return n;
            return "(unnamed)";
        }

        private static string TryReadAnyProperty(dynamic obj, string[] candidates)
        {
            foreach (var c in candidates)
                if (TryReadValue(obj, c, out string v) && !string.IsNullOrEmpty(v))
                    return $"{c}='{Truncate(v, 80)}'";
            return "(none of " + string.Join(", ", candidates) + " worked)";
        }

        private static bool TryReadValue(dynamic obj, string propertyName, out string value)
        {
            try
            {
                value = obj.Properties(propertyName)?.Value?.ToString() ?? "";
                return true;
            }
            catch
            {
                value = "";
                return false;
            }
        }

        private static T? TryGet<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "...";
        }

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
