using System;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Single source of the term-type CHANGEABILITY semantics: which parts of a column's
    /// Physical_Data_Type may change, per canonical term-type concept resolved from the
    /// glossary (TERM_TYPE_MAP admin rows -> BUSINESS_TERM / AMORPH_DATA_TYPE /
    /// AMORPH_DATA_LENGTH / AMORPH).
    /// <para>
    /// Consumers: <c>EnforceTermTypePolicy</c> (reverts a disallowed edit - its switch keeps
    /// extra revert nuance, e.g. a base change under AMORPH_DATA_LENGTH reverts BOTH parts
    /// because erwin's combo clears the length) and <c>EnforceAllowedDatatypeWhitelist</c>
    /// (gates the Datatype Library picker: the type combo is disabled when the base may not
    /// change, the parameter field when the length may not change, and the picker is not
    /// shown at all when both are locked). Keep this mapping aligned with the policy switch.
    /// </para>
    /// </summary>
    public static class TermTypeLocks
    {
        public const string BusinessTerm = "BUSINESS_TERM";
        public const string AmorphDataType = "AMORPH_DATA_TYPE";
        public const string AmorphDataLength = "AMORPH_DATA_LENGTH";
        public const string Amorph = "AMORPH";

        /// <summary>
        /// Lock flags for a canonical term-type code. <c>lockBase</c> = the BASE type may not
        /// change; <c>lockLength</c> = the length/precision parameter may not change.
        /// Null/empty, AMORPH, and unknown codes are unconstrained (fail-open, mirroring
        /// EnforceTermTypePolicy's null/AMORPH/default branches).
        /// </summary>
        public static (bool lockBase, bool lockLength) Get(string canonical)
        {
            switch ((canonical ?? string.Empty).Trim().ToUpperInvariant())
            {
                case BusinessTerm: return (true, true);       // fully glossary-authoritative
                case AmorphDataType: return (false, true);    // base free, length fixed
                case AmorphDataLength: return (true, false);  // base fixed, length free
                case Amorph:
                default: return (false, false);               // unconstrained / unknown
            }
        }

        /// <summary>
        /// True when a datatype value respects the locked parts of the term-authoritative
        /// value: same base when <paramref name="lockBase"/>, same length/precision when
        /// <paramref name="lockLength"/> (base case-insensitive, length ordinal - mirrors
        /// EnforceTermTypePolicy's comparisons). Used to vet a remembered picker value before
        /// re-enforcing it on erwin's duplicate combo-commit.
        /// </summary>
        public static bool Honors(string candidate, string authoritative, bool lockBase, bool lockLength)
        {
            if (!lockBase && !lockLength) return true;
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(authoritative)) return false;

            var c = DataTypeParser.Parse(candidate);
            var a = DataTypeParser.Parse(authoritative);
            if (lockBase && !string.Equals(c.Base, a.Base, StringComparison.OrdinalIgnoreCase)) return false;
            if (lockLength && !string.Equals(c.Length ?? string.Empty, a.Length ?? string.Empty, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>
        /// The DURABLE locked length/precision for a length-locked term. GLOSSARY-FIRST: the term
        /// mapping's PHYSICAL_DATA_TYPE (<paramref name="glossaryValue"/>) DEFINES the fixed
        /// length, so when it carries one, it wins outright; the snapshot baseline
        /// (<paramref name="snapshotValue"/>) only anchors when the mapping has no datatype.
        /// Snapshot-first was tried first (2026-07-10 morning) and proved wrong in the field the
        /// same day: the baseline can be POISONED - a term-canonical-unresolved window or a
        /// whitelist-allowed delayed re-commit absorbed NUMERIC(555) into the snapshot, after
        /// which every "revert" restored 555 instead of the term-fixed 5 ("Restored:
        /// NUMERIC(555)" bug). The baseline can also legitimately lose the length entirely
        /// (parameterless BIGINT picked under AMORPH_DATA_TYPE). Returns "" when neither side
        /// carries a length (nothing to pin).
        /// </summary>
        public static string ResolveLockedLength(string snapshotValue, string glossaryValue)
        {
            string fromGlossary = DataTypeParser.Parse(glossaryValue ?? string.Empty).Length;
            if (!string.IsNullOrEmpty(fromGlossary)) return fromGlossary;
            return DataTypeParser.Parse(snapshotValue ?? string.Empty).Length ?? string.Empty;
        }
    }
}
