#nullable enable

using System;
using System.Text;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Thrown when a <c>{token}</c> in a Template naming rule cannot be
    /// resolved at runtime (target property absent, related object missing,
    /// the source value is null/whitespace, or the token's function chain is
    /// malformed / produced an empty result). The "Template" rule type is
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
    /// Template rule GENERATES a target value (an erwin property OR a UDP)
    /// from a template string. One token is
    /// <c>{ SOURCE ( "|" FUNC ( ":" ARG )* )* }</c>:
    /// <list type="bullet">
    ///   <item><c>{PropertyCode}</c> - a property of the SAME object (e.g. <c>{Physical_Name}</c>).</item>
    ///   <item><c>{Alias.PropertyCode}</c> - a property of a RELATED object reached
    ///   through the admin <c>MC_OBJECT_RELATION</c> alias (e.g. <c>{Table.Physical_Name}</c>
    ///   for a column's parent table).</item>
    ///   <item><c>{Udp:Name}</c> - a UDP of the SAME object. The <c>Udp:</c> prefix
    ///   is checked BEFORE the dot-split because a UDP name may contain both
    ///   <c>:</c> and <c>.</c> (only <c>|</c> and <c>}</c> are forbidden in names,
    ///   they are grammar separators).</item>
    /// </list>
    /// Each token may carry a pipe chain of string functions applied left to
    /// right to the resolved source value: <c>trim</c>, <c>upper</c>, <c>lower</c>
    /// (0 args), <c>left:n</c>, <c>right:n</c> (first/last n chars),
    /// <c>substr:start:len</c> (0-based, n &gt;= 0), <c>replace:a:b</c> (ordinal,
    /// all occurrences). Example:
    /// <c>PRE_{Udp:Owner|trim|upper}_{Name|left:3}-{Table.Name|replace:_:-}</c>.
    /// Text outside tokens is preserved verbatim; a token-free template is a
    /// valid constant. The class is deliberately free of any erwin/COM
    /// dependency so the grammar and the no-fallback semantics are unit-tested
    /// in isolation; the runtime supplies the reader delegates that bridge
    /// to SCAPI.
    /// </summary>
    public static class NamingTemplateEngine
    {
        // One token = "{" + (one or more non-"}" chars) + "}". The "+" forbids
        // an empty "{}" from matching, so a stray "{}" is left as literal text
        // rather than treated as a token. PROPERTY_CODE / ALIAS / UDP names
        // never contain "}" so this never under-matches a real token.
        private static readonly Regex TokenPattern = new Regex(@"\{([^}]+)\}", RegexOptions.Compiled);

        // {Udp:Name} source marker. Checked case-insensitively so admin-typed
        // "udp:" / "UDP:" resolve too. Property codes never contain ':' so the
        // prefix cannot collide with a legitimate {PropertyCode} token.
        private const string UdpSourcePrefix = "Udp:";

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
        /// <param name="udpReader">Reads a UDP of the SAME object by UDP name for
        /// <c>{Udp:Name}</c> tokens; returns null/empty when the value is absent.
        /// When null, any <c>{Udp:...}</c> token is a hard error (contexts that
        /// cannot read UDPs must not silently drop the token).</param>
        /// <exception cref="TemplateResolutionException">A token's source value is
        /// null or whitespace, its function chain is malformed, or the chain
        /// produced an empty result (NO-FALLBACK: nothing is written).</exception>
        public static string Render(
            string? template,
            Func<string, string?> ownPropReader,
            Func<string, string, string?> relatedPropReader,
            Func<string, string?>? udpReader = null)
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

                // Pipe-split FIRST: segment 0 is the SOURCE, the rest are the
                // function chain. Source names may not contain '|' (grammar
                // separator), so a plain split is exact.
                string[] segments = token.Split('|');
                string source = segments[0].Trim();

                string? value = ResolveSource(source, token, ownPropReader, relatedPropReader, udpReader);

                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new TemplateResolutionException(
                        token,
                        $"Template token '{{{token}}}' resolved to an empty value");
                }

                // Function chain, left to right. Malformed segments throw; a
                // chain that eats the whole value (e.g. substr past the end,
                // replace-to-nothing) is caught by the final empty check below.
                for (int i = 1; i < segments.Length; i++)
                    value = ApplyFunction(value!, segments[i], token);

                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new TemplateResolutionException(
                        token,
                        $"Template token '{{{token}}}': function chain produced an empty value");
                }

                output.Append(value);
            }

            // Trailing literal text after the last token.
            output.Append(template, lastIndex, template.Length - lastIndex);
            return output.ToString();
        }

        /// <summary>
        /// Resolve a token's SOURCE segment to its raw value. Dispatch order
        /// matters: the <c>Udp:</c> prefix is checked before the dot-split
        /// because a UDP name may itself contain a dot.
        /// </summary>
        private static string? ResolveSource(
            string source,
            string token,
            Func<string, string?> ownPropReader,
            Func<string, string, string?> relatedPropReader,
            Func<string, string?>? udpReader)
        {
            if (source.StartsWith(UdpSourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                // {Udp:Name} - everything after the first ':' is the UDP name
                // (the name itself may contain further ':' characters).
                string udpName = source.Substring(UdpSourcePrefix.Length).Trim();
                if (udpName.Length == 0)
                {
                    throw new TemplateResolutionException(
                        token, $"Template token '{{{token}}}': empty UDP name after '{UdpSourcePrefix}'");
                }
                if (udpReader == null)
                {
                    throw new TemplateResolutionException(
                        token, $"Template token '{{{token}}}': {{Udp:...}} sources are not supported in this context");
                }
                return udpReader(udpName);
            }

            int dot = source.IndexOf('.');
            if (dot >= 0)
            {
                // {Alias.PropertyCode} - related object. Split on the FIRST
                // dot only: aliases and property codes never contain a dot,
                // so the first dot is the unambiguous separator.
                string alias = source.Substring(0, dot).Trim();
                string propertyCode = source.Substring(dot + 1).Trim();
                return relatedPropReader(alias, propertyCode);
            }

            // {PropertyCode} - same object.
            return ownPropReader(source);
        }

        /// <summary>
        /// Apply one pipe-chain function segment (e.g. <c>upper</c>,
        /// <c>left:3</c>, <c>replace:_:-</c>) to <paramref name="value"/>.
        /// Function names are case-insensitive. upper/lower use the INVARIANT
        /// culture on purpose: template output feeds DB identifiers and UDPs,
        /// and tr-TR casing (dotted/dotless I) must never leak in - same
        /// decision as the glossary's case-insensitive comparer.
        /// Any malformed segment (unknown name, wrong arg count, non-integer or
        /// negative numeric arg, empty replace search string) throws: a broken
        /// admin-authored chain must surface, never half-apply.
        /// </summary>
        private static string ApplyFunction(string value, string funcSegment, string token)
        {
            // ARG separator is ':'; args therefore cannot contain ':' (nor '|'
            // / '}', the outer separators). replace args are used verbatim -
            // no trim - so "replace: :_" can collapse spaces to underscores.
            string[] parts = funcSegment.Split(':');
            string name = parts[0].Trim();
            int argCount = parts.Length - 1;

            switch (name.ToLowerInvariant())
            {
                case "trim":
                    RequireArgCount(0);
                    return value.Trim();

                case "upper":
                    RequireArgCount(0);
                    return value.ToUpperInvariant();

                case "lower":
                    RequireArgCount(0);
                    return value.ToLowerInvariant();

                case "left":
                {
                    RequireArgCount(1);
                    int n = ParseCount(parts[1]);
                    return value.Length <= n ? value : value.Substring(0, n);
                }

                case "right":
                {
                    RequireArgCount(1);
                    int n = ParseCount(parts[1]);
                    return value.Length <= n ? value : value.Substring(value.Length - n);
                }

                case "substr":
                {
                    RequireArgCount(2);
                    int start = ParseCount(parts[1]);
                    int len = ParseCount(parts[2]);
                    if (start >= value.Length) return string.Empty;
                    return value.Substring(start, Math.Min(len, value.Length - start));
                }

                case "replace":
                {
                    RequireArgCount(2);
                    string search = parts[1];
                    string replacement = parts[2];
                    if (search.Length == 0)
                    {
                        throw new TemplateResolutionException(
                            token, $"Template token '{{{token}}}': replace needs a non-empty search string");
                    }
                    return value.Replace(search, replacement); // ordinal, all occurrences
                }

                default:
                    throw new TemplateResolutionException(
                        token, $"Template token '{{{token}}}': unknown function '{name}'");
            }

            void RequireArgCount(int expected)
            {
                if (argCount != expected)
                {
                    throw new TemplateResolutionException(
                        token,
                        $"Template token '{{{token}}}': function '{name}' expects {expected} argument(s), got {argCount}");
                }
            }

            int ParseCount(string raw)
            {
                if (!int.TryParse(raw.Trim(), out int n) || n < 0)
                {
                    throw new TemplateResolutionException(
                        token,
                        $"Template token '{{{token}}}': function '{name}' needs a non-negative integer, got '{raw}'");
                }
                return n;
            }
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

        /// <summary>
        /// True when <paramref name="template"/> contains an OWN token (a
        /// <c>{PropertyCode}</c> with no <c>Alias.</c> prefix) whose SOURCE
        /// equals <paramref name="propertyCode"/> - i.e. the template reads the
        /// very property it is going to write. Such a rule is self-referential:
        /// under <c>FILL_MODE=Always</c> each render feeds its own previous
        /// output back in, so the value grows without bound and a transaction is
        /// written every heartbeat. The caller MUST refuse such a rule (a
        /// related token like <c>{Table.Physical_Name}</c> is the correct way to
        /// seed a name). The comparison strips any pipe chain first -
        /// <c>{Physical_Name|upper}</c> still reads Physical_Name. Related
        /// tokens (<c>{Alias.PropertyCode}</c>) and <c>{Udp:...}</c> tokens are
        /// never self-referential for a PROPERTY target and are ignored here.
        /// </summary>
        public static bool ReferencesOwnProperty(string? template, string? propertyCode)
        {
            if (string.IsNullOrEmpty(template) || string.IsNullOrWhiteSpace(propertyCode))
                return false;

            string target = propertyCode!.Trim();
            foreach (Match m in TokenPattern.Matches(template!))
            {
                string source = ExtractSource(m);
                if (source.StartsWith(UdpSourcePrefix, StringComparison.OrdinalIgnoreCase))
                    continue; // UDP source, cannot read a property target
                if (source.IndexOf('.') >= 0) continue; // related token, not own
                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// UDP-target counterpart of <see cref="ReferencesOwnProperty"/>: true
        /// when <paramref name="template"/> contains a <c>{Udp:Name}</c> token
        /// whose name equals <paramref name="udpName"/> - the template would
        /// read the very UDP it is going to write, which under
        /// <c>FILL_MODE=Always</c> is the same unbounded-growth runaway. The
        /// caller MUST refuse such a rule. Property and related tokens are
        /// ignored (they never read a UDP target).
        /// </summary>
        public static bool ReferencesOwnUdp(string? template, string? udpName)
        {
            if (string.IsNullOrEmpty(template) || string.IsNullOrWhiteSpace(udpName))
                return false;

            string target = udpName!.Trim();
            foreach (Match m in TokenPattern.Matches(template!))
            {
                string source = ExtractSource(m);
                if (!source.StartsWith(UdpSourcePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string name = source.Substring(UdpSourcePrefix.Length).Trim();
                if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Token match -> its SOURCE segment (pipe chain stripped, trimmed).</summary>
        private static string ExtractSource(Match m)
        {
            string token = m.Groups[1].Value.Trim();
            int pipe = token.IndexOf('|');
            return (pipe >= 0 ? token.Substring(0, pipe) : token).Trim();
        }
    }
}
