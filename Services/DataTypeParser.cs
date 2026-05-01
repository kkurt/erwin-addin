using System;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Splits a physical data type expression into its base type and length component, and
    /// recombines them. Used by the term-type policy to compare which part of the type the
    /// user changed (BUSINESS_TERM / AMORPH_DATA_TYPE / AMORPH_DATA_LENGTH / AMORPH).
    ///
    /// Conventions:
    ///   "VARCHAR2(200)"             -> base="VARCHAR2",  length="200",  suffix=""
    ///   "NUMBER(10,2)"              -> base="NUMBER",    length="10,2", suffix=""
    ///   "TIMESTAMP(6) WITH TIME ZONE" -> base="TIMESTAMP", length="6", suffix=" WITH TIME ZONE"
    ///   "DATE"                      -> base="DATE",      length=null,   suffix=""
    ///   "VARCHAR2(200 CHAR)"        -> base="VARCHAR2",  length="200 CHAR", suffix=""
    ///
    /// Comparison and recombination are case-preserving on the base/suffix portions; the
    /// caller decides case sensitivity for equality checks.
    /// </summary>
    public static class DataTypeParser
    {
        // ^\s*([\w$#]+)        -> base type (allow $/# for Oracle quirks)
        //   (?:\s*\(([^)]*)\))? -> optional (length) — captures whatever is inside parens
        //   (.*)$              -> trailing modifiers like " WITH TIME ZONE"
        private static readonly Regex _re = new Regex(
            @"^\s*([\w$#]+)\s*(?:\(([^)]*)\))?(.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public readonly struct Parts
        {
            public string Base { get; }
            public string Length { get; }
            public string Suffix { get; }

            public Parts(string baseType, string length, string suffix)
            {
                Base = baseType ?? string.Empty;
                Length = length;
                Suffix = suffix ?? string.Empty;
            }

            public bool HasLength => Length != null;
        }

        /// <summary>
        /// Parse a Physical_Data_Type string into base / length / suffix. Returns a Parts
        /// with Base = original string and Length = null when the input is empty or doesn't
        /// match the expected shape — callers should treat that as "type only, no length".
        /// </summary>
        public static Parts Parse(string dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return new Parts(string.Empty, null, string.Empty);

            var m = _re.Match(dataType);
            if (!m.Success) return new Parts(dataType.Trim(), null, string.Empty);

            string baseType = m.Groups[1].Value;
            string length = m.Groups[2].Success ? m.Groups[2].Value : null;
            string suffix = m.Groups[3].Success ? m.Groups[3].Value : string.Empty;

            return new Parts(baseType, length, suffix);
        }

        /// <summary>
        /// Reassemble a base / length / suffix triple back into the canonical
        /// "BASE(length)suffix" form. Length null/empty omits the parens entirely so we
        /// don't write "VARCHAR2()" when reverting a length-only change.
        /// </summary>
        public static string Format(Parts parts)
        {
            string baseType = parts.Base ?? string.Empty;
            string length = parts.Length;
            string suffix = parts.Suffix ?? string.Empty;

            if (string.IsNullOrEmpty(length))
                return baseType + suffix;
            return baseType + "(" + length + ")" + suffix;
        }

        /// <summary>
        /// Convenience: split a Physical_Data_Type and ask whether the base or length differs
        /// between two values. Comparisons are ordinal-ignore-case for the base, ordinal for
        /// the length (lengths are usually numeric and case doesn't matter, but "CHAR"
        /// modifier is significant).
        /// </summary>
        public static (bool baseChanged, bool lengthChanged) Diff(string oldDataType, string newDataType)
        {
            var oldParts = Parse(oldDataType);
            var newParts = Parse(newDataType);

            bool baseChanged = !string.Equals(oldParts.Base, newParts.Base, StringComparison.OrdinalIgnoreCase);
            bool lengthChanged = !string.Equals(oldParts.Length ?? string.Empty, newParts.Length ?? string.Empty, StringComparison.Ordinal);

            return (baseChanged, lengthChanged);
        }
    }
}
