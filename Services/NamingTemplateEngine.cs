#nullable enable

using System;
using System.Collections.Generic;
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
    ///   <c>:</c> and <c>.</c>. A <c>|</c> in a name has to be escaped
    ///   (<c>{Udp:A\|B}</c>); <c>}</c> has no escaped form at all.</item>
    ///   <item><c>{Current}</c> - RESERVED: the TARGET's own current value, for
    ///   templates that must reshape a value in place (e.g.
    ///   <c>{Table.Physical_Name}_{Current}</c> keeps a column name prefixed with
    ///   its table). It is the only sanctioned way to read the target, and it
    ///   converges instead of looping - see <see cref="Render"/>.</item>
    /// </list>
    /// Each token may carry a pipe chain of string functions applied left to
    /// right to the resolved source value: <c>trim</c>, <c>upper</c>, <c>lower</c>
    /// (0 args), <c>left:n</c>, <c>right:n</c> (first/last n chars),
    /// <c>substr:start:len</c> (0-based, n &gt;= 0), <c>replace:a:b</c> (ordinal,
    /// all occurrences), <c>split:sep:index</c> (0-based piece of an ordinal
    /// split; <c>{Name|split:_:0}</c> on <c>ORDER_ITEM_ID</c> gives <c>ORDER</c>).
    /// Example:
    /// <c>PRE_{Udp:Owner|trim|upper}_{Name|left:3}-{Table.Name|replace:_:-}</c>.
    /// Text outside tokens is preserved verbatim; a token-free template is a
    /// valid constant. The class is deliberately free of any erwin/COM
    /// dependency so the grammar and the no-fallback semantics are unit-tested
    /// in isolation; the runtime supplies the reader delegates that bridge
    /// to SCAPI.
    /// <para><b>Escaping.</b> <c>|</c> and <c>:</c> are the grammar's own
    /// separators, so a template that needs one as DATA escapes it with a
    /// backslash: <c>{Udp:X|split:\|:0}</c> cuts a pipe-delimited value, and
    /// <c>\\</c> is a literal backslash. Only those three sequences exist -
    /// any other <c>\x</c> is a hard error rather than a silently-kept literal,
    /// so a typo cannot change what a rule means. <c>}</c> has NO escaped form:
    /// it terminates the token and stays forbidden inside one.</para>
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

        // {Current} - the reserved SOURCE that stands for the target's own
        // current value. Reserved means it always wins over a same-named
        // PROPERTY_CODE / relation ALIAS; a survey of every MetaRepo* admin DB
        // found no such name, so nothing existing is shadowed.
        private const string CurrentSourceName = "Current";

        // TEMPLATE_FILL_MODE values, shared by ShouldWrite and the {Current}
        // compatibility check so the spelling lives in exactly one place.
        private const string FillModeAlways = "Always";
        private const string FillModeOnlyIfEmpty = "OnlyIfEmpty";

        // Escape character. '|' and ':' are the grammar's separators, so a
        // template that needs one as DATA writes "\|" / "\:"; "\\" is a literal
        // backslash. Nothing else is escapable - see Unescape.
        private const char EscapeChar = '\\';

        /// <summary>
        /// Split on UNESCAPED occurrences of <paramref name="separator"/>. Escape
        /// sequences are carried through to the pieces untouched: unescaping happens
        /// once, at the leaf that actually consumes the text (a name or an argument),
        /// so a two-level parse never has to reason about half-unescaped input.
        /// <para>An escape always swallows the character behind it, which is what
        /// makes <c>\\|</c> an escaped backslash followed by a REAL separator: without
        /// that, <c>replace:a\\:b</c> could not mean "replace 'a\' with 'b'".</para>
        /// </summary>
        private static List<string> SplitUnescaped(string text, char separator)
        {
            var parts = new List<string>(4);
            var piece = new StringBuilder(text.Length);

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == EscapeChar && i + 1 < text.Length)
                {
                    // Keep both characters: the sequence is decoded later, and a
                    // trailing lone '\' deliberately falls through to Unescape so
                    // the dangling-escape error is reported once, in one place.
                    piece.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }

                if (c == separator)
                {
                    parts.Add(piece.ToString());
                    piece.Clear();
                    continue;
                }

                piece.Append(c);
            }

            parts.Add(piece.ToString());
            return parts;
        }

        /// <summary>
        /// Decode the escape sequences in a leaf (a source name or a function
        /// argument). Recognised: <c>\|</c>, <c>\:</c>, <c>\\</c>.
        /// </summary>
        /// <exception cref="TemplateResolutionException">The text ends in a lone
        /// backslash, or carries an escape that is not one of the three. Unknown
        /// escapes are refused rather than kept verbatim: a silently-literal
        /// <c>\x</c> would let a typo change what a rule writes, and the whole
        /// Template type is NO-FALLBACK by contract.
        /// <para><c>\}</c> is not among them and needs no branch of its own:
        /// <see cref="TokenPattern"/> ends the token at the <c>}</c>, so the
        /// attempt always reaches here as a dangling backslash instead.</para></exception>
        private static string Unescape(string raw, string token)
        {
            if (!TryUnescape(raw, out string value, out string? error))
            {
                throw new TemplateResolutionException(
                    token, $"Template token '{{{token}}}': {error}");
            }
            return value;
        }

        /// <summary>
        /// Non-throwing <see cref="Unescape"/>, for the pure predicates
        /// (<see cref="ReferencesOwnProperty"/>, <see cref="ReferencesOwnUdp"/>) that
        /// answer a yes/no question about a template and must not fail on a malformed
        /// one - the render reports it, precisely, a moment later.
        /// </summary>
        private static bool TryUnescape(string raw, out string value, out string? error)
        {
            error = null;

            // Fast path: the overwhelming majority of templates carry no escape at all.
            if (raw.IndexOf(EscapeChar) < 0)
            {
                value = raw;
                return true;
            }

            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c != EscapeChar)
                {
                    sb.Append(c);
                    continue;
                }

                if (i + 1 >= raw.Length)
                {
                    // Two authors land here, and the message has to serve both: one
                    // wrote a literal trailing backslash, the other tried to escape
                    // '}' - and that attempt ARRIVES as a dangling escape, because
                    // TokenPattern ended the token at the '}' before this ever ran.
                    error = $"'{raw}' ends with a dangling '\\'. Write '\\\\' for a literal "
                          + "backslash; '}' cannot be escaped at all, it ends the token";
                    value = raw;
                    return false;
                }

                char next = raw[i + 1];
                if (next == '|' || next == ':' || next == EscapeChar)
                {
                    sb.Append(next);
                    i++;
                    continue;
                }

                error = $"unknown escape '\\{next}' in '{raw}'; only \\| \\: and \\\\ exist";
                value = raw;
                return false;
            }

            value = sb.ToString();
            return true;
        }

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
        /// <param name="currentValue">The TARGET property/UDP's current value, used
        /// only by the reserved <c>{Current}</c> token. Leave null in contexts that
        /// have no target; a <c>{Current}</c> token then fails as an empty source
        /// rather than resolving to something arbitrary.</param>
        /// <exception cref="TemplateResolutionException">A token's source value is
        /// null or whitespace, its function chain is malformed, the chain produced
        /// an empty result, the template holds more than one <c>{Current}</c>, or
        /// the chain on <c>{Current}</c> is not idempotent (NO-FALLBACK: nothing is
        /// written).</exception>
        /// <remarks>
        /// <para><b>{Current} semantics (shape recognizer).</b> A template holding
        /// <c>{Current}</c> is read as <c>L {Current} R</c>: L and R are rendered
        /// normally (they may carry any other tokens), then the SEED is taken from
        /// the target's current value - the middle when the value already starts
        /// with L and ends with R, otherwise the whole value. The result is
        /// <c>L + chain(seed) + R</c>.</para>
        /// <para>That makes the rule a FIXED POINT: the first render wraps the raw
        /// value (<c>abc</c> -> <c>Table2_abc</c>), and every later render strips L
        /// and R back off and rebuilds the identical string, so the caller's
        /// "rendered == current" check stops it. Exactly one write, no unbounded
        /// growth - which is why <c>{Current}</c> exists and a plain self-referential
        /// <c>{Physical_Name}</c> targeting Physical_Name is still refused: that one
        /// re-wraps its own previous output on every heartbeat.</para>
        /// </remarks>
        public static string Render(
            string? template,
            Func<string, string?> ownPropReader,
            Func<string, string, string?> relatedPropReader,
            Func<string, string?>? udpReader = null,
            string? currentValue = null)
        {
            if (ownPropReader == null) throw new ArgumentNullException(nameof(ownPropReader));
            if (relatedPropReader == null) throw new ArgumentNullException(nameof(relatedPropReader));
            if (string.IsNullOrEmpty(template)) return string.Empty;

            Match? currentToken = FindCurrentToken(template!);
            if (currentToken != null)
            {
                return RenderShaped(
                    template!, currentToken, currentValue, ownPropReader, relatedPropReader, udpReader);
            }

            return RenderTokens(template!, ownPropReader, relatedPropReader, udpReader);
        }

        /// <summary>
        /// Straight left-to-right token substitution - the whole grammar EXCEPT
        /// <c>{Current}</c>, which needs the target value and is handled by
        /// <see cref="RenderShaped"/> before this runs.
        /// </summary>
        private static string RenderTokens(
            string template,
            Func<string, string?> ownPropReader,
            Func<string, string, string?> relatedPropReader,
            Func<string, string?>? udpReader)
        {
            var output = new StringBuilder(template.Length + 16);
            int lastIndex = 0;

            foreach (Match m in TokenPattern.Matches(template))
            {
                // Literal text between the previous token and this one.
                output.Append(template, lastIndex, m.Index - lastIndex);
                lastIndex = m.Index + m.Length;

                string token = m.Groups[1].Value.Trim();

                // Pipe-split FIRST: segment 0 is the SOURCE, the rest are the
                // function chain. Only UNESCAPED pipes separate, so "\|" stays
                // inside whichever segment carries it.
                List<string> segments = SplitUnescaped(token, '|');
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
                value = ApplyChain(value!, segments, token);

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
        /// Locate the single <c>{Current}</c> token, or null when the template
        /// has none.
        /// </summary>
        /// <exception cref="TemplateResolutionException">The template holds two or
        /// more <c>{Current}</c> tokens. The shape split would be ambiguous (which
        /// occurrence carries the old value?), so this is refused outright rather
        /// than resolved by a convention nobody can predict.</exception>
        private static Match? FindCurrentToken(string template)
        {
            Match? found = null;
            foreach (Match m in TokenPattern.Matches(template))
            {
                if (!IsCurrentSource(ExtractSource(m))) continue;
                if (found != null)
                {
                    throw new TemplateResolutionException(
                        CurrentSourceName,
                        $"Template holds more than one '{{{CurrentSourceName}}}' token; exactly one is allowed");
                }
                found = m;
            }
            return found;
        }

        /// <summary>
        /// Render a template that contains <c>{Current}</c>, per the fixed-point
        /// rule documented on <see cref="Render"/>: split at the token, render the
        /// left and right halves normally, recover the seed from
        /// <paramref name="currentValue"/>, and rebuild <c>L + chain(seed) + R</c>.
        /// </summary>
        private static string RenderShaped(
            string template,
            Match currentToken,
            string? currentValue,
            Func<string, string?> ownPropReader,
            Func<string, string, string?> relatedPropReader,
            Func<string, string?>? udpReader)
        {
            // The halves cannot contain another {Current} (FindCurrentToken has
            // already rejected that), so plain token rendering is enough here.
            string left = RenderTokens(
                template.Substring(0, currentToken.Index), ownPropReader, relatedPropReader, udpReader);
            string right = RenderTokens(
                template.Substring(currentToken.Index + currentToken.Length),
                ownPropReader, relatedPropReader, udpReader);

            string token = currentToken.Groups[1].Value.Trim();
            string seed = ExtractSeed(currentValue ?? string.Empty, left, right);

            if (string.IsNullOrWhiteSpace(seed))
            {
                // NO-FALLBACK: {Current} RESHAPES an existing value, it never
                // invents one. An empty target means the rule cannot run yet.
                throw new TemplateResolutionException(
                    token,
                    $"Template token '{{{token}}}' resolved to an empty value: the target has no current value to reshape");
            }

            List<string> segments = SplitUnescaped(token, '|');
            string value = ApplyChain(seed, segments, token);

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new TemplateResolutionException(
                    token, $"Template token '{{{token}}}': function chain produced an empty value");
            }

            // Convergence check. The seed is fed back in on every later render,
            // so the chain has to be a fixed point of itself - "upper" is,
            // "replace:a:aa" is not and would grow the value forever. Proving it
            // here (one extra pure pass) is what keeps the runaway that plain
            // self-reference caused from sneaking back in through a pipe.
            if (segments.Count > 1)
            {
                string twice = ApplyChain(value, segments, token);
                if (!string.Equals(twice, value, StringComparison.Ordinal))
                {
                    throw new TemplateResolutionException(
                        token,
                        $"Template token '{{{token}}}': the function chain is not idempotent " +
                        $"('{value}' becomes '{twice}' when applied again), so the value would never settle");
                }
            }

            return left + value + right;
        }

        /// <summary>
        /// Recover the value that <c>{Current}</c> stands for. When
        /// <paramref name="currentValue"/> is already in the template's shape
        /// (starts with <paramref name="left"/>, ends with <paramref name="right"/>,
        /// long enough to hold both) the seed is the middle - that is what makes a
        /// second render reproduce the first one exactly. Otherwise the value has
        /// not been shaped yet and the whole of it is the seed.
        /// </summary>
        private static string ExtractSeed(string currentValue, string left, string right)
        {
            // Ordinal on purpose: same comparison the caller's "already correct"
            // check uses, so the two can never disagree about a case difference.
            if (currentValue.Length >= left.Length + right.Length
                && currentValue.StartsWith(left, StringComparison.Ordinal)
                && currentValue.EndsWith(right, StringComparison.Ordinal))
            {
                return currentValue.Substring(left.Length, currentValue.Length - left.Length - right.Length);
            }

            return currentValue;
        }

        /// <summary>
        /// Apply a token's pipe chain (segment 0 is the SOURCE and is skipped)
        /// left to right.
        /// </summary>
        private static string ApplyChain(string value, List<string> segments, string token)
        {
            for (int i = 1; i < segments.Count; i++)
                value = ApplyFunction(value, segments[i], token);
            return value;
        }

        /// <summary>
        /// True when a token's SOURCE segment is the reserved <c>Current</c>. Compared
        /// against the ENCODED source deliberately: "Current" holds none of the three
        /// escapable characters, so it has no alternative spelling to normalise, and
        /// an escape anywhere in the name (<c>{Curr\ent}</c>) is a malformed token the
        /// render must report rather than a disguised <c>{Current}</c>.
        /// </summary>
        private static bool IsCurrentSource(string source)
            => string.Equals(source, CurrentSourceName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolve a token's SOURCE segment to its raw value. Dispatch order
        /// matters: the <c>Udp:</c> prefix is checked before the dot-split
        /// because a UDP name may itself contain a dot.
        /// <para>Structure is read off the ESCAPED text and only the extracted
        /// names are unescaped, so an escaped separator cannot be mistaken for a
        /// structural one: <c>{Udp\:Name}</c> is the property literally called
        /// "Udp:Name", while <c>{Udp:A\|B}</c> is the UDP called "A|B".</para>
        /// </summary>
        private static string? ResolveSource(
            string source,
            string token,
            Func<string, string?> ownPropReader,
            Func<string, string, string?> relatedPropReader,
            Func<string, string?>? udpReader)
        {
            if (IsCurrentSource(source))
            {
                // Defensive: Render routes {Current} to RenderShaped before this
                // runs, so reaching here means a future caller bypassed it.
                // Falling through to ownPropReader("Current") would quietly read
                // a property nobody defined - fail loudly instead.
                throw new TemplateResolutionException(
                    token,
                    $"Template token '{{{token}}}': '{CurrentSourceName}' is a reserved source that only resolves against a target value");
            }

            if (source.StartsWith(UdpSourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                // {Udp:Name} - everything after the first ':' is the UDP name
                // (the name itself may contain further ':' characters).
                string udpName = Unescape(source.Substring(UdpSourcePrefix.Length).Trim(), token);
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
                string alias = Unescape(source.Substring(0, dot).Trim(), token);
                string propertyCode = Unescape(source.Substring(dot + 1).Trim(), token);
                return relatedPropReader(alias, propertyCode);
            }

            // {PropertyCode} - same object.
            return ownPropReader(Unescape(source, token));
        }

        /// <summary>
        /// Apply one pipe-chain function segment (e.g. <c>upper</c>,
        /// <c>left:3</c>, <c>replace:_:-</c>, <c>split:_:0</c>) to
        /// <paramref name="value"/>.
        /// Function names are case-insensitive. upper/lower use the INVARIANT
        /// culture on purpose: template output feeds DB identifiers and UDPs,
        /// and tr-TR casing (dotted/dotless I) must never leak in - same
        /// decision as the glossary's case-insensitive comparer.
        /// Any malformed segment (unknown name, wrong arg count, non-integer or
        /// negative numeric arg, empty replace search string or split separator,
        /// bad escape sequence) throws: a broken admin-authored chain must
        /// surface, never half-apply.
        /// </summary>
        private static string ApplyFunction(string value, string funcSegment, string token)
        {
            // ARG separator is ':', so an argument that needs one as data escapes
            // it ("split:\::0" cuts on a colon); '}' has no escaped form. The
            // string args of replace and split are used verbatim - no trim - so
            // "replace: :_" collapses spaces to underscores and "split: :0" takes
            // the first word. Only parts[0] (the function NAME) is trimmed, which
            // is what makes "{Name | split: :0}" still see a single space as the
            // separator.
            List<string> parts = SplitUnescaped(funcSegment, ':');
            // The name is matched against a closed set, so it is left encoded on
            // purpose: an escape there can never be valid, and reporting the
            // author's own text ("unknown function 'spl\it'") is the clearer error.
            string name = parts[0].Trim();
            int argCount = parts.Count - 1;

            // Decode every argument once, up front. Doing it here rather than per
            // function keeps the numeric args honest too: "left:\:" reaches
            // ParseCount as ":" and gets the integer error, not an escape error
            // from a place that was not expecting text.
            for (int i = 1; i < parts.Count; i++)
                parts[i] = Unescape(parts[i], token);

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

                case "split":
                {
                    // split:<separator>:<index> - the index'th piece of an
                    // ordinal split. The separator is used VERBATIM (no trim),
                    // exactly like replace's arguments, so a single space is a
                    // legitimate separator: {Name|split: :0} is "first word".
                    // A grammar separator is reachable through its escape:
                    // {Udp:X|split:\|:0} cuts a pipe-delimited value.
                    RequireArgCount(2);
                    string separator = parts[1];
                    if (separator.Length == 0)
                    {
                        // Same class of error as replace's empty search string:
                        // splitting on nothing has no defined meaning, and
                        // guessing one (whitespace? every char?) would make the
                        // rule mean different things in admin and here.
                        throw new TemplateResolutionException(
                            token, $"Template token '{{{token}}}': split needs a non-empty separator");
                    }
                    // 0-based to match substr:start:len - a second convention
                    // would be a coin flip for whoever writes the next rule.
                    int index = ParseCount(parts[2]);
                    string[] pieces = value.Split(separator, StringSplitOptions.None);
                    // Out of range yields empty, the natural result of the
                    // operation (same as substr with start >= length) - NOT a
                    // fallback. The caller's "chain produced an empty value"
                    // check then aborts the render, so nothing half-formed is
                    // ever written.
                    return index < pieces.Length ? pieces[index] : string.Empty;
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

            if (string.Equals(mode, FillModeAlways, StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(mode, FillModeOnlyIfEmpty, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(currentValue);

            unknownMode = true;
            return false;
        }

        /// <summary>
        /// True when <paramref name="template"/> uses the reserved
        /// <c>{Current}</c> source. Callers need this to read the target's value
        /// BEFORE rendering and to detect the contradictory fill mode below.
        /// </summary>
        public static bool ContainsCurrentToken(string? template)
        {
            if (string.IsNullOrEmpty(template)) return false;

            foreach (Match m in TokenPattern.Matches(template!))
            {
                if (IsCurrentSource(ExtractSource(m))) return true;
            }
            return false;
        }

        /// <summary>
        /// True when a rule pairs <c>{Current}</c> with
        /// <c>TEMPLATE_FILL_MODE=OnlyIfEmpty</c> - a combination that can never
        /// write anything: OnlyIfEmpty runs only while the target is empty, and an
        /// empty target gives <c>{Current}</c> nothing to reshape. Admin refuses
        /// the pair at save time; the runtime checks it too so a rule inserted by
        /// hand is skipped with a clear reason instead of failing every render.
        /// </summary>
        public static bool CurrentTokenConflictsWithFillMode(string? template, string? fillMode)
        {
            return ContainsCurrentToken(template)
                && string.Equals((fillMode ?? "").Trim(), FillModeOnlyIfEmpty, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when <paramref name="template"/> contains an OWN token (a
        /// <c>{PropertyCode}</c> with no <c>Alias.</c> prefix) whose SOURCE
        /// equals <paramref name="propertyCode"/> - i.e. the template reads the
        /// very property it is going to write. Such a rule is self-referential:
        /// under <c>FILL_MODE=Always</c> each render feeds its own previous
        /// output back in, so the value grows without bound and a transaction is
        /// written every heartbeat. The caller MUST refuse such a rule -
        /// <c>{Current}</c> is the sanctioned way to fold the target's own value
        /// back in, because it converges after one write. The comparison strips
        /// any pipe chain first - <c>{Physical_Name|upper}</c> still reads
        /// Physical_Name. Related tokens (<c>{Alias.PropertyCode}</c>),
        /// <c>{Udp:...}</c> tokens and <c>{Current}</c> are never self-referential
        /// for a PROPERTY target and are ignored here.
        /// </summary>
        public static bool ReferencesOwnProperty(string? template, string? propertyCode)
        {
            if (string.IsNullOrEmpty(template) || string.IsNullOrWhiteSpace(propertyCode))
                return false;

            string target = propertyCode!.Trim();
            foreach (Match m in TokenPattern.Matches(template!))
            {
                string source = ExtractSource(m);
                if (IsCurrentSource(source))
                    continue; // reserved shape token, converges by construction
                if (source.StartsWith(UdpSourcePrefix, StringComparison.OrdinalIgnoreCase))
                    continue; // UDP source, cannot read a property target
                if (source.IndexOf('.') >= 0) continue; // related token, not own
                // A malformed escape cannot match a real property code, so the
                // encoded text is compared as-is and the render reports the fault.
                if (!TryUnescape(source, out string name, out _)) continue;
                if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
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
        /// caller MUST refuse such a rule; <c>{Current}</c> is the converging
        /// alternative. Property, related and <c>{Current}</c> tokens are ignored
        /// (they never read a UDP target).
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
                // Same as above: an escaped '|' in the UDP name has to be decoded
                // before the comparison, or "{Udp:A\|B}" would not be recognised as
                // reading the UDP literally called "A|B".
                if (!TryUnescape(source.Substring(UdpSourcePrefix.Length).Trim(), out string name, out _))
                    continue;
                if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Token match -> its SOURCE segment (pipe chain stripped, trimmed), still
        /// ENCODED. Callers read structure off it (the <c>Udp:</c> prefix, the dot)
        /// exactly as <see cref="ResolveSource"/> does and unescape only the leaf
        /// name they end up comparing, so the two can never disagree about what a
        /// token names.
        /// </summary>
        private static string ExtractSource(Match m)
        {
            string token = m.Groups[1].Value.Trim();
            return SplitUnescaped(token, '|')[0].Trim();
        }
    }
}
