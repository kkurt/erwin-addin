using System.Globalization;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Parses the PARENTHESIZED content of a STRUCTURED-parametrized datatype
    /// (DATATYPE_LIBRARY.PARAMETRIZATION_TYPE = STRUCTURED, WP 323) into its parts.
    /// Grammar: <c>p[,s][ suffix]</c> - a required positive length/precision, an optional
    /// signed scale after a comma, and an optional length-semantics suffix after the first
    /// whitespace following the numbers. Examples: "22,5" / "15 CHAR" / "22,5 XYZ".
    /// <para>
    /// This is the paren-INSIDE grammar. Do NOT confuse it with
    /// <see cref="DataTypeParser"/>.Parts.Suffix, which is the paren-OUTSIDE tail
    /// ("TIMESTAMP(6) WITH TIME ZONE" -&gt; suffix " WITH TIME ZONE"): DataTypeParser hands the
    /// paren content over VERBATIM and this parser takes it from there.
    /// </para>
    /// </summary>
    public static class StructuredParamParser
    {
        // ^\s*(\d+)                  p: unsigned digits (raw erwin values arrive unnormalized)
        // \s*(?:,\s*([+-]?\d+))?     optional ",s": whitespace allowed around the comma
        //                            ("10 , 2" is accepted), scale may be signed (Oracle -84)
        // (?:\s+(\S.*?))?            optional suffix: everything from the first whitespace after
        //                            the numbers; may contain inner spaces and digit fragments
        //                            ("22,5 XYZ 2" -> suffix "XYZ 2"). "5CHAR" (no space) does
        //                            NOT match and is rejected.
        // \s*$                       trailing whitespace ignored
        private static readonly Regex ContentPattern = new Regex(
            @"^\s*(\d+)\s*(?:,\s*([+-]?\d+))?(?:\s+(\S.*?))?\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// Try to parse paren content into length/precision <paramref name="p"/>, optional scale
        /// <paramref name="s"/> (null when absent; may be negative) and the trimmed
        /// length-semantics <paramref name="suffix"/> (empty string, never null, when absent).
        /// Returns false when the content does not match the grammar or a number overflows Int32.
        /// </summary>
        public static bool TryParse(string content, out int p, out int? s, out string suffix)
        {
            p = 0;
            s = null;
            suffix = string.Empty;
            if (string.IsNullOrWhiteSpace(content)) return false;

            var m = ContentPattern.Match(content);
            if (!m.Success) return false;

            // Overflowing digits (e.g. "99999999999") are a parse failure, not an exception:
            // the caller surfaces them as an invalid parameter.
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out p))
                return false;

            if (m.Groups[2].Success)
            {
                if (!int.TryParse(m.Groups[2].Value, NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out int scale))
                    return false;
                s = scale;
            }

            if (m.Groups[3].Success) suffix = m.Groups[3].Value.Trim();
            return true;
        }
    }
}
