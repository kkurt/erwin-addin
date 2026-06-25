#nullable enable

using System;
using System.Text;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Thrown when a <c>{token}</c> in a Template naming rule cannot be
    /// resolved at runtime (target property absent, related object missing,
    /// or the source value is null/whitespace). The "Template" rule type is
    /// NO-FALLBACK by contract: a half-rendered value must never reach the
    /// model, so the renderer aborts the whole render instead of substituting
    /// an empty string. The caller catches this, surfaces the rule's
    /// ERROR_MESSAGE (or a sensible default) to the log, and skips the write.
    /// </summary>
    public sealed class TemplateResolutionException : Exception
    {
        /// <summary>The verbatim token (inside the braces) that failed, e.g. "Table.Physical_Name".</summary>
        public string Token { get; }

        public TemplateResolutionException(string token, string message)
            : base(message)
        {
            Token = token ?? "";
        }
    }

    /// <summary>
    /// Pure (SCAPI-free) renderer for the "Template" naming rule type. A
    /// Template rule GENERATES a target property value from a template string
    /// whose tokens read properties of the same object or a related object:
    /// <list type="bullet">
    ///   <item><c>{PropertyCode}</c> - a property of the SAME object (e.g. <c>{Physical_Name}</c>).</item>
    ///   <item><c>{Alias.PropertyCode}</c> - a property of a RELATED object reached
    ///   through the admin <c>MC_OBJECT_RELATION</c> alias (e.g. <c>{Table.Physical_Name}</c>
    ///   for a column's parent table).</item>
    /// </list>
    /// Text outside tokens is preserved verbatim; a token-free template is a
    /// valid constant. The class is deliberately free of any erwin/COM
    /// dependency so the grammar and the no-fallback semantics are unit-tested
    /// in isolation; the runtime supplies the two reader delegates that bridge
    /// to SCAPI.
    /// </summary>
    public static class NamingTemplateEngine
    {
        // One token = "{" + (one or more non-"}" chars) + "}". The "+" forbids
        // an empty "{}" from matching, so a stray "{}" is left as literal text
        // rather than treated as a token. PROPERTY_CODE / ALIAS values never
        // contain "}" so this never under-matches a real token.
        private static readonly Regex TokenPattern = new Regex(@"\{([^}]+)\}", RegexOptions.Compiled);

        /// <summary>
        /// Render <paramref name="template"/>, substituting each token via the
        /// supplied readers. Returns the fully-rendered string.
        /// </summary>
        /// <param name="template">The VALUE_TEMPLATE text. A null/empty template
        /// renders to an empty string (the caller decides whether to write it).</param>
        /// <param name="ownPropReader">Reads a property of the SAME object by its
        /// PROPERTY_CODE; returns null/empty when the value is absent.</param>
        /// <param name="relatedPropReader">Reads a property of a RELATED object,
        /// given (alias, propertyCode); returns null/empty when the alias cannot
        /// be navigated or the value is absent. The runtime throws from inside
        /// this delegate for an unknown alias so an unsupported relation is a
        /// hard error, never a silent skip.</param>
        /// <exception cref="TemplateResolutionException">A token's source value is
        /// null or whitespace (NO-FALLBACK: nothing is written).</exception>
        public static string Render(
            string? template,
            Func<string, string?> ownPropReader,
            Func<string, string, string?> relatedPropReader)
        {
            if (ownPropReader == null) throw new ArgumentNullException(nameof(ownPropReader));
            if (relatedPropReader == null) throw new ArgumentNullException(nameof(relatedPropReader));
            if (string.IsNullOrEmpty(template)) return string.Empty;

            var output = new StringBuilder(template!.Length + 16);
            int lastIndex = 0;

            foreach (Match m in TokenPattern.Matches(template))
            {
                // Literal text between the previous token and this one.
                output.Append(template, lastIndex, m.Index - lastIndex);
                lastIndex = m.Index + m.Length;

                string token = m.Groups[1].Value.Trim();
                int dot = token.IndexOf('.');

                string? value;
                if (dot >= 0)
                {
                    // {Alias.PropertyCode} - related object. Split on the FIRST
                    // dot only: aliases and property codes never contain a dot,
                    // so the first dot is the unambiguous separator.
                    string alias = token.Substring(0, dot).Trim();
                    string propertyCode = token.Substring(dot + 1).Trim();
                    value = relatedPropReader(alias, propertyCode);
                }
                else
                {
                    // {PropertyCode} - same object.
                    value = ownPropReader(token);
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new TemplateResolutionException(
                        token,
                        $"Template token '{{{token}}}' resolved to an empty value");
                }

                output.Append(value);
            }

            // Trailing literal text after the last token.
            output.Append(template, lastIndex, template.Length - lastIndex);
            return output.ToString();
        }

        /// <summary>
        /// Decide whether a rendered value should be written to the target
        /// property, per <c>TEMPLATE_FILL_MODE</c>:
        /// <list type="bullet">
        ///   <item><c>Always</c> - overwrite unconditionally.</item>
        ///   <item><c>OnlyIfEmpty</c> - write only when the current target value
        ///   is empty/whitespace (do not clobber a human-authored value).</item>
        /// </list>
        /// </summary>
        /// <param name="fillMode">The rule's TEMPLATE_FILL_MODE (case-insensitive).</param>
        /// <param name="currentValue">The target property's current value.</param>
        /// <param name="unknownMode">Set true when <paramref name="fillMode"/> is
        /// neither Always nor OnlyIfEmpty. The caller must SKIP the rule in that
        /// case (no guessing a default) - admin validates the mode, so an unknown
        /// value here means a malformed rule that must not silently overwrite.</param>
        /// <returns>True to write; false to skip.</returns>
        public static bool ShouldWrite(string? fillMode, string? currentValue, out bool unknownMode)
        {
            unknownMode = false;
            string mode = (fillMode ?? "").Trim();

            if (string.Equals(mode, "Always", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(mode, "OnlyIfEmpty", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(currentValue);

            unknownMode = true;
            return false;
        }
    }
}
