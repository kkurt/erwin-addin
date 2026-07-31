#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// The admin OBJECT_TYPEs for which this add-in actually RUNS
    /// <see cref="NamingRuleKind.Template"/> rules, and the single place that
    /// knows the list.
    ///
    /// <para>Why this exists: on 2026-07-31 an admin defined two live
    /// <c>OBJECT_TYPE=MODEL</c> Template rules. They loaded, they were printed in
    /// the connect-time rule dump, and then nothing happened - because the only
    /// two appliers in the add-in ask for "Column" and "PRIMARY KEY" by string
    /// literal. Nothing anywhere said "there is no applier for MODEL", so the
    /// failure looked identical to a broken rule and cost a full support round
    /// trip. The appliers now request their rules through the constants below and
    /// <see cref="ModelConfigForm"/> compares the loaded rule set against
    /// <see cref="SupportedObjectTypes"/> at connect, so an unhandled object type
    /// reports itself instead of failing silently.</para>
    ///
    /// <para>Adding an applier means adding its object type here in the same
    /// change - that is the point. A constant used by the applier itself cannot
    /// drift from the literal the applier passes, because it IS that literal.</para>
    /// </summary>
    public static class TemplateApplierRegistry
    {
        /// <summary>Columns (SCAPI Attribute). Applied per column change.</summary>
        public const string Column = "Column";

        /// <summary>The table-owned primary key (SCAPI Key_Group, Key_Group_Type == "PK").</summary>
        public const string PrimaryKey = "PRIMARY KEY";

        /// <summary>The model root. Applied when a MODEL-scoped UDP value changes.</summary>
        public const string Model = "MODEL";

        private static readonly string[] Supported = { Column, PrimaryKey, Model };

        /// <summary>
        /// Normalised (see <see cref="ApprovalBlockingRuleGate.NormalizeObjectType"/>)
        /// object types that have a Template applier. Normalisation is delegated
        /// rather than re-implemented because this repo already carries several
        /// admin-OBJECT_TYPE mappings that disagree with each other; a new one
        /// would be the next bug.
        /// </summary>
        public static IReadOnlyCollection<string> SupportedObjectTypes { get; } =
            Supported.Select(ApprovalBlockingRuleGate.NormalizeObjectType).ToArray();

        /// <summary>
        /// True when a Template rule with this admin OBJECT_TYPE will actually be
        /// run by some applier. Comparison is on the normalised form, so
        /// "Primary Key", "PRIMARY_KEY" and "PRIMARY KEY" all answer the same.
        /// </summary>
        public static bool HasApplier(string? objectType)
        {
            if (string.IsNullOrWhiteSpace(objectType)) return false;
            string normalized = ApprovalBlockingRuleGate.NormalizeObjectType(objectType);
            return SupportedObjectTypes.Contains(normalized, StringComparer.Ordinal);
        }
    }
}
