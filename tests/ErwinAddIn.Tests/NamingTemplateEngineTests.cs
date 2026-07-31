using System;
using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for <see cref="NamingTemplateEngine"/>, the pure renderer
/// behind the "Template" naming rule type: token grammar ({Own}, {Alias.Prop}
/// and {Udp:Name}), the per-token pipe function chain (trim/upper/lower/
/// left/right/substr/replace/split), literal-text preservation, the NO-FALLBACK
/// contract (an unresolvable token or malformed/emptying chain aborts the
/// whole render), and the TEMPLATE_FILL_MODE write decision. SCAPI navigation
/// lives in the runtime applier and is not exercised here on purpose - this
/// guards the grammar/semantics in isolation.
/// </summary>
public class NamingTemplateEngineTests
{
    // A reader that fails every lookup; handy for "no tokens" assertions.
    private static string? NoOwn(string code) => throw new InvalidOperationException($"unexpected own read '{code}'");
    private static string? NoRelated(string alias, string code) => throw new InvalidOperationException($"unexpected related read '{alias}.{code}'");
    private static string? NoUdp(string name) => throw new InvalidOperationException($"unexpected UDP read '{name}'");

    private static Func<string, string?> Own(Dictionary<string, string?> map) =>
        code => map.TryGetValue(code, out var v) ? v : null;

    private static Func<string, string, string?> Related(Dictionary<string, string?> map) =>
        (alias, code) => map.TryGetValue($"{alias}.{code}", out var v) ? v : null;

    private static Func<string, string?> Udp(Dictionary<string, string?> map) =>
        name => map.TryGetValue(name, out var v) ? v : null;

    // ---- Render: own-object tokens ---------------------------------------

    [Fact]
    public void Render_substitutes_own_property_token()
    {
        var own = Own(new Dictionary<string, string?> { ["Physical_Name"] = "CUSTOMER" });

        string result = NamingTemplateEngine.Render("PK_{Physical_Name}", own, NoRelated);

        result.Should().Be("PK_CUSTOMER");
    }

    [Fact]
    public void Render_substitutes_multiple_own_tokens_and_keeps_literals()
    {
        var own = Own(new Dictionary<string, string?>
        {
            ["Physical_Name"] = "ORDER",
            ["Owner"] = "dbo",
        });

        string result = NamingTemplateEngine.Render("{Owner}.{Physical_Name}_v2", own, NoRelated);

        result.Should().Be("dbo.ORDER_v2");
    }

    // ---- Render: related-object tokens -----------------------------------

    [Fact]
    public void Render_substitutes_related_property_token()
    {
        var related = Related(new Dictionary<string, string?> { ["Table.Physical_Name"] = "CUSTOMER" });

        string result = NamingTemplateEngine.Render(
            "Bu aciklama \"{Table.Physical_Name}\" kolonu icin bir aciklamadir",
            NoOwn,
            related);

        result.Should().Be("Bu aciklama \"CUSTOMER\" kolonu icin bir aciklamadir");
    }

    [Fact]
    public void Render_mixes_own_and_related_tokens()
    {
        var own = Own(new Dictionary<string, string?> { ["Physical_Name"] = "ID" });
        var related = Related(new Dictionary<string, string?> { ["Table.Physical_Name"] = "CUSTOMER" });

        string result = NamingTemplateEngine.Render("{Table.Physical_Name}.{Physical_Name}", own, related);

        result.Should().Be("CUSTOMER.ID");
    }

    [Fact]
    public void Render_splits_alias_on_first_dot_only()
    {
        // The property code segment keeps any further dots verbatim; only the
        // first dot separates alias from property code.
        var related = Related(new Dictionary<string, string?> { ["Table.A.B"] = "X" });

        string result = NamingTemplateEngine.Render("{Table.A.B}", NoOwn, related);

        result.Should().Be("X");
    }

    [Fact]
    public void Render_trims_whitespace_inside_token()
    {
        var own = Own(new Dictionary<string, string?> { ["Physical_Name"] = "T" });

        string result = NamingTemplateEngine.Render("{ Physical_Name }", own, NoRelated);

        result.Should().Be("T");
    }

    // ---- Render: literals / no tokens ------------------------------------

    [Fact]
    public void Render_returns_constant_template_unchanged()
    {
        string result = NamingTemplateEngine.Render("just a constant", NoOwn, NoRelated);

        result.Should().Be("just a constant");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Render_returns_empty_for_null_or_empty_template(string? template)
    {
        string result = NamingTemplateEngine.Render(template, NoOwn, NoRelated);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Render_leaves_empty_braces_as_literal()
    {
        // "{}" has no inner chars so it is not a token; it survives verbatim.
        string result = NamingTemplateEngine.Render("a{}b", NoOwn, NoRelated);

        result.Should().Be("a{}b");
    }

    // ---- Render: NO-FALLBACK ---------------------------------------------

    [Fact]
    public void Render_throws_when_own_token_resolves_null()
    {
        var own = Own(new Dictionary<string, string?> { ["Physical_Name"] = null });

        Action act = () => NamingTemplateEngine.Render("PK_{Physical_Name}", own, NoRelated);

        act.Should().Throw<TemplateResolutionException>()
            .Which.Token.Should().Be("Physical_Name");
    }

    [Fact]
    public void Render_throws_when_own_token_resolves_whitespace()
    {
        var own = Own(new Dictionary<string, string?> { ["Physical_Name"] = "   " });

        Action act = () => NamingTemplateEngine.Render("PK_{Physical_Name}", own, NoRelated);

        act.Should().Throw<TemplateResolutionException>()
            .Which.Token.Should().Be("Physical_Name");
    }

    [Fact]
    public void Render_throws_when_related_token_unresolved()
    {
        // relatedPropReader returns null (e.g. no parent or unknown value).
        var related = Related(new Dictionary<string, string?>());

        Action act = () => NamingTemplateEngine.Render("{Table.Physical_Name}", NoOwn, related);

        act.Should().Throw<TemplateResolutionException>()
            .Which.Token.Should().Be("Table.Physical_Name");
    }

    [Fact]
    public void Render_propagates_reader_thrown_exception_for_unknown_alias()
    {
        // The runtime throws from inside the related reader for an unknown
        // alias (no-fallback at the navigation layer); Render must not swallow it.
        Func<string, string, string?> throwing = (alias, code) =>
            throw new InvalidOperationException($"unknown alias '{alias}'");

        Action act = () => NamingTemplateEngine.Render("{Bogus.Name}", NoOwn, throwing);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown alias 'Bogus'*");
    }

    // ---- ShouldWrite ------------------------------------------------------

    [Theory]
    [InlineData("Always", "existing", true)]
    [InlineData("Always", "", true)]
    [InlineData("always", "existing", true)] // case-insensitive
    public void ShouldWrite_always_overwrites(string mode, string current, bool expected)
    {
        bool write = NamingTemplateEngine.ShouldWrite(mode, current, out bool unknown);

        write.Should().Be(expected);
        unknown.Should().BeFalse();
    }

    [Theory]
    [InlineData("OnlyIfEmpty", "", true)]
    [InlineData("OnlyIfEmpty", "   ", true)]
    [InlineData("OnlyIfEmpty", null, true)]
    [InlineData("OnlyIfEmpty", "has value", false)]
    [InlineData("onlyifempty", "has value", false)] // case-insensitive
    public void ShouldWrite_onlyifempty_respects_current_value(string mode, string? current, bool expected)
    {
        bool write = NamingTemplateEngine.ShouldWrite(mode, current, out bool unknown);

        write.Should().Be(expected);
        unknown.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Sometimes")]
    public void ShouldWrite_unknown_mode_skips_and_flags(string? mode)
    {
        bool write = NamingTemplateEngine.ShouldWrite(mode, "anything", out bool unknown);

        write.Should().BeFalse();
        unknown.Should().BeTrue();
    }

    // ---- ReferencesOwnProperty (self-referential / runaway guard) ----------

    [Fact]
    public void ReferencesOwnProperty_own_token_equal_to_target_is_self_referential()
    {
        // 'PK_{Physical_Name}' targeting Physical_Name = the runaway that grew
        // PK_PK_PK_... every heartbeat.
        NamingTemplateEngine.ReferencesOwnProperty("PK_{Physical_Name}", "Physical_Name")
            .Should().BeTrue();
    }

    [Fact]
    public void ReferencesOwnProperty_is_case_insensitive_on_the_token()
    {
        NamingTemplateEngine.ReferencesOwnProperty("PK_{physical_name}", "Physical_Name")
            .Should().BeTrue();
    }

    [Fact]
    public void ReferencesOwnProperty_related_token_to_same_name_is_not_self_referential()
    {
        // {Table.Physical_Name} reads the PARENT, not the target - the correct form.
        NamingTemplateEngine.ReferencesOwnProperty("PK_{Table.Physical_Name}", "Physical_Name")
            .Should().BeFalse();
    }

    [Fact]
    public void ReferencesOwnProperty_own_token_to_other_property_is_not_self_referential()
    {
        NamingTemplateEngine.ReferencesOwnProperty("{Name}_PK", "Physical_Name")
            .Should().BeFalse();
    }

    [Fact]
    public void ReferencesOwnProperty_detects_self_reference_among_multiple_tokens()
    {
        NamingTemplateEngine.ReferencesOwnProperty("{Table.Name}_{Physical_Name}", "Physical_Name")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("", "Physical_Name")]
    [InlineData(null, "Physical_Name")]
    [InlineData("literal only, no tokens", "Physical_Name")]
    [InlineData("{Physical_Name}", "")]
    [InlineData("{Physical_Name}", null)]
    public void ReferencesOwnProperty_safe_on_empty_inputs(string? template, string? propertyCode)
    {
        NamingTemplateEngine.ReferencesOwnProperty(template, propertyCode)
            .Should().BeFalse();
    }

    // ---- Render: {Udp:Name} source (migration 9) ---------------------------

    [Fact]
    public void Render_substitutes_udp_token()
    {
        var udp = Udp(new Dictionary<string, string?> { ["Owner"] = "dbo" });

        string result = NamingTemplateEngine.Render("PRE_{Udp:Owner}", NoOwn, NoRelated, udp);

        result.Should().Be("PRE_dbo");
    }

    [Fact]
    public void Render_udp_prefix_is_case_insensitive()
    {
        var udp = Udp(new Dictionary<string, string?> { ["Owner"] = "dbo" });

        NamingTemplateEngine.Render("{udp:Owner}", NoOwn, NoRelated, udp).Should().Be("dbo");
        NamingTemplateEngine.Render("{UDP:Owner}", NoOwn, NoRelated, udp).Should().Be("dbo");
    }

    [Fact]
    public void Render_udp_name_may_contain_colon()
    {
        // Only the FIRST colon (the one in the "Udp:" prefix) separates; the
        // rest of the source is the name verbatim.
        var udp = Udp(new Dictionary<string, string?> { ["Ana:Grup"] = "X" });

        NamingTemplateEngine.Render("{Udp:Ana:Grup}", NoOwn, NoRelated, udp).Should().Be("X");
    }

    [Fact]
    public void Render_udp_name_may_contain_dot_and_is_not_treated_as_alias()
    {
        // Dispatch order: the Udp: prefix is checked BEFORE the dot-split, so
        // a dotted UDP name never routes to the related-object reader.
        var udp = Udp(new Dictionary<string, string?> { ["My.Name"] = "V" });

        NamingTemplateEngine.Render("{Udp:My.Name}", NoOwn, NoRelated, udp).Should().Be("V");
    }

    [Fact]
    public void Render_throws_when_udp_token_used_without_reader()
    {
        Action act = () => NamingTemplateEngine.Render("{Udp:Owner}", NoOwn, (a, c) => null);

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*not supported*");
    }

    [Fact]
    public void Render_throws_when_udp_value_empty()
    {
        var udp = Udp(new Dictionary<string, string?> { ["Owner"] = "" });

        Action act = () => NamingTemplateEngine.Render("{Udp:Owner}", NoOwn, NoRelated, udp);

        act.Should().Throw<TemplateResolutionException>()
            .Which.Token.Should().Be("Udp:Owner");
    }

    [Fact]
    public void Render_throws_when_udp_name_missing_after_prefix()
    {
        var udp = Udp(new Dictionary<string, string?>());

        Action act = () => NamingTemplateEngine.Render("{Udp:}", NoOwn, NoRelated, udp);

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*empty UDP name*");
    }

    // ---- Render: pipe function chain --------------------------------------

    [Theory]
    [InlineData("{Name|trim}", "  ab  ", "ab")]
    [InlineData("{Name|upper}", "TabLo", "TABLO")]
    [InlineData("{Name|lower}", "TabLo", "tablo")]
    [InlineData("{Name|left:3}", "CUSTOMER", "CUS")]
    [InlineData("{Name|right:2}", "CUSTOMER", "ER")]
    [InlineData("{Name|substr:2:3}", "CUSTOMER", "STO")]
    [InlineData("{Name|replace:_:-}", "A_B_C", "A-B-C")]
    [InlineData("{Name|split:_:0}", "ORDER_ITEM_ID", "ORDER")]
    public void Render_applies_single_function(string template, string sourceValue, string expected)
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = sourceValue });

        NamingTemplateEngine.Render(template, own, NoRelated).Should().Be(expected);
    }

    [Fact]
    public void Render_applies_chain_left_to_right()
    {
        // trim first, then upper, then left:3 - order matters.
        var own = Own(new Dictionary<string, string?> { ["Name"] = "  customer  " });

        NamingTemplateEngine.Render("{Name|trim|upper|left:3}", own, NoRelated).Should().Be("CUS");
    }

    [Fact]
    public void Render_upper_lower_use_invariant_culture()
    {
        // tr-TR would turn "i" into a dotted capital İ; invariant must produce
        // plain ASCII I (template output feeds DB identifiers).
        var own = Own(new Dictionary<string, string?> { ["Name"] = "istanbul" });

        NamingTemplateEngine.Render("{Name|upper}", own, NoRelated).Should().Be("ISTANBUL");
    }

    [Theory]
    [InlineData("{Name|left:99}", "AB", "AB")]   // n beyond length -> whole string
    [InlineData("{Name|right:99}", "AB", "AB")]
    [InlineData("{Name|substr:1:99}", "ABC", "BC")] // len clipped to remainder
    public void Render_length_functions_clip_gracefully(string template, string sourceValue, string expected)
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = sourceValue });

        NamingTemplateEngine.Render(template, own, NoRelated).Should().Be(expected);
    }

    [Fact]
    public void Render_function_names_are_case_insensitive()
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = "ab" });

        NamingTemplateEngine.Render("{Name|UPPER}", own, NoRelated).Should().Be("AB");
    }

    [Fact]
    public void Render_functions_apply_to_related_and_udp_sources_too()
    {
        var related = Related(new Dictionary<string, string?> { ["Table.Name"] = "MY_TABLE" });
        var udp = Udp(new Dictionary<string, string?> { ["Owner"] = "  dbo  " });

        NamingTemplateEngine.Render("{Table.Name|replace:_:-}", NoOwn, related).Should().Be("MY-TABLE");
        NamingTemplateEngine.Render("{Udp:Owner|trim|upper}", NoOwn, NoRelated, udp).Should().Be("DBO");
    }

    [Fact]
    public void Render_spec_example_end_to_end()
    {
        // The example from the requirement:
        // PRE_{Udp:Owner|trim|upper}_{Name|left:3}-{Table.Name|replace:_:-}
        var own = Own(new Dictionary<string, string?> { ["Name"] = "Customer" });
        var related = Related(new Dictionary<string, string?> { ["Table.Name"] = "MY_TABLE" });
        var udp = Udp(new Dictionary<string, string?> { ["Owner"] = " dbo " });

        string result = NamingTemplateEngine.Render(
            "PRE_{Udp:Owner|trim|upper}_{Name|left:3}-{Table.Name|replace:_:-}",
            own, related, udp);

        result.Should().Be("PRE_DBO_Cus-MY-TABLE");
    }

    // ---- split:<separator>:<index> ----------------------------------------

    [Theory]
    [InlineData("{Name|split:_:0}", "ORDER_ITEM_ID", "ORDER")]
    [InlineData("{Name|split:_:1}", "ORDER_ITEM_ID", "ITEM")]
    [InlineData("{Name|split:_:2}", "ORDER_ITEM_ID", "ID")]
    [InlineData("{Name|split:__:1}", "A__B__C", "B")]          // multi-char separator
    [InlineData("{Name|split:-:0}", "ORDER_ITEM", "ORDER_ITEM")] // separator absent -> whole value
    [InlineData("{Name|split:_:1}", "_LEADING", "LEADING")]      // leading separator -> piece 0 is empty
    public void Render_split_takes_the_indexed_piece(string template, string sourceValue, string expected)
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = sourceValue });

        NamingTemplateEngine.Render(template, own, NoRelated).Should().Be(expected);
    }

    [Fact]
    public void Render_split_separator_is_verbatim_so_a_single_space_works()
    {
        // The separator must NOT be trimmed (same rule as replace's args),
        // otherwise "first word" would be inexpressible.
        var own = Own(new Dictionary<string, string?> { ["Name"] = "ORDER ITEM" });

        NamingTemplateEngine.Render("{Name|split: :0}", own, NoRelated).Should().Be("ORDER");
        NamingTemplateEngine.Render("{Name|split: :1}", own, NoRelated).Should().Be("ITEM");
    }

    [Fact]
    public void Render_split_separator_survives_spaces_around_the_function_name()
    {
        // Only parts[0] (the NAME) is trimmed, so padding the segment must not
        // silently turn a space separator into an empty one.
        var own = Own(new Dictionary<string, string?> { ["Name"] = "ORDER ITEM" });

        NamingTemplateEngine.Render("{ Name | split: :1 }", own, NoRelated).Should().Be("ITEM");
    }

    [Fact]
    public void Render_split_is_ordinal_and_case_sensitive()
    {
        // Separator matching must not inherit the function name's
        // case-insensitivity - "x" and "X" are different separators.
        var own = Own(new Dictionary<string, string?> { ["Name"] = "AxBXC" });

        NamingTemplateEngine.Render("{Name|split:x:1}", own, NoRelated).Should().Be("BXC");
    }

    [Fact]
    public void Render_split_chains_with_other_functions()
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = "  order_item_id  " });

        NamingTemplateEngine.Render("{Name|trim|split:_:0|upper}", own, NoRelated).Should().Be("ORDER");
    }

    [Theory]
    [InlineData("{Name|split:_:9}", "ORDER_ITEM_ID")] // index past the last piece
    [InlineData("{Name|split:_:0}", "_LEADING")]      // leading separator -> piece 0 is empty
    [InlineData("{Name|split:_:1}", "TRAILING_")]     // trailing separator -> last piece is empty
    public void Render_split_that_yields_an_empty_piece_aborts_the_render(string template, string sourceValue)
    {
        // The FUNCTION yields "" (the natural result, same as substr past the
        // end); the NO-FALLBACK contract then turns that into a hard error, so
        // no half-formed name reaches the model.
        var own = Own(new Dictionary<string, string?> { ["Name"] = sourceValue });

        Action act = () => NamingTemplateEngine.Render(template, own, NoRelated);

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*produced an empty value*");
    }

    [Theory]
    [InlineData("{Name|split:_}")]        // missing index
    [InlineData("{Name|split}")]          // no args at all
    [InlineData("{Name|split:_:0:1}")]    // too many args
    [InlineData("{Name|split::0}")]       // empty separator
    [InlineData("{Name|split:_:x}")]      // non-integer index
    [InlineData("{Name|split:_:-1}")]     // negative index
    public void Render_throws_on_malformed_split(string template)
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = "ORDER_ITEM_ID" });

        Action act = () => NamingTemplateEngine.Render(template, own, NoRelated);

        act.Should().Throw<TemplateResolutionException>();
    }

    [Fact]
    public void Render_split_empty_separator_names_the_reason()
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = "ORDER_ITEM_ID" });

        Action act = () => NamingTemplateEngine.Render("{Name|split::0}", own, NoRelated);

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*non-empty separator*");
    }

    [Fact]
    public void Render_split_works_on_related_and_udp_sources()
    {
        var related = Related(new Dictionary<string, string?> { ["Table.Name"] = "DWH_ORDER" });
        var udp = Udp(new Dictionary<string, string?> { ["Owner"] = "dbo.schema" });

        NamingTemplateEngine.Render("{Table.Name|split:_:1}", NoOwn, related).Should().Be("ORDER");
        NamingTemplateEngine.Render("{Udp:Owner|split:.:0}", NoOwn, NoRelated, udp).Should().Be("dbo");
    }

    [Fact]
    public void Render_split_on_current_is_a_fixed_point()
    {
        // {Current} feeds its own output back in, so a chain is only allowed
        // when it settles. split does: the first piece of a value that no
        // longer holds the separator is the value itself.
        var own = Own(new Dictionary<string, string?>());

        string first = NamingTemplateEngine.Render(
            "{Current|split:_:0}", own, NoRelated, NoUdp, "ORDER_ITEM_ID");
        first.Should().Be("ORDER");

        NamingTemplateEngine.Render("{Current|split:_:0}", own, NoRelated, NoUdp, first)
            .Should().Be(first);
    }

    [Fact]
    public void Render_split_on_current_that_never_settles_is_refused()
    {
        // "A_B" -> "B", and "B" -> "" on the next pass: the chain is not a
        // fixed point, so the idempotency guard must refuse it rather than let
        // the value oscillate.
        var own = Own(new Dictionary<string, string?>());

        Action act = () => NamingTemplateEngine.Render(
            "{Current|split:_:1}", own, NoRelated, NoUdp, "A_B");

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*not idempotent*");
    }

    [Fact]
    public void Render_pipeless_templates_are_unchanged()
    {
        // Back-compat: the live rule 1167 shape must render byte-identically.
        var related = Related(new Dictionary<string, string?> { ["Table.Physical_Name"] = "CUSTOMER" });

        NamingTemplateEngine.Render("PK_{Table.Physical_Name}", NoOwn, related).Should().Be("PK_CUSTOMER");
    }

    // ---- Render: malformed / emptying chains are hard errors ----------------

    [Theory]
    [InlineData("{Name|bogus}")]          // unknown function
    [InlineData("{Name|upper:1}")]        // 0-arg function given an arg
    [InlineData("{Name|left}")]           // missing arg
    [InlineData("{Name|left:x}")]         // non-integer arg
    [InlineData("{Name|left:-1}")]        // negative arg
    [InlineData("{Name|substr:1}")]       // wrong arg count
    [InlineData("{Name|replace:a}")]      // wrong arg count
    [InlineData("{Name|replace::x}")]     // empty search string
    [InlineData("{Name|}")]               // empty function segment
    public void Render_throws_on_malformed_function(string template)
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = "VALUE" });

        Action act = () => NamingTemplateEngine.Render(template, own, NoRelated);

        act.Should().Throw<TemplateResolutionException>();
    }

    [Theory]
    [InlineData("{Name|substr:99:5}")]    // start beyond end -> empty
    [InlineData("{Name|replace:AB:}")]    // replace-to-nothing eats the value
    public void Render_throws_when_chain_produces_empty(string template)
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = "AB" });

        Action act = () => NamingTemplateEngine.Render(template, own, NoRelated);

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*produced an empty value*");
    }

    [Fact]
    public void Render_source_empty_check_precedes_chain()
    {
        // An empty SOURCE keeps the original "resolved to an empty value"
        // message even when a chain follows.
        var own = Own(new Dictionary<string, string?> { ["Name"] = "" });

        Action act = () => NamingTemplateEngine.Render("{Name|upper}", own, NoRelated);

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*resolved to an empty value*");
    }

    // ---- Self-ref guards: pipe-aware + UDP variant -------------------------

    [Fact]
    public void ReferencesOwnProperty_sees_through_pipe_chain()
    {
        // {Physical_Name|upper} still READS Physical_Name - self-referential.
        NamingTemplateEngine.ReferencesOwnProperty("{Physical_Name|upper}", "Physical_Name")
            .Should().BeTrue();
    }

    [Fact]
    public void ReferencesOwnProperty_ignores_udp_tokens()
    {
        // A {Udp:X} token never reads a PROPERTY target, even with the same name.
        NamingTemplateEngine.ReferencesOwnProperty("{Udp:Physical_Name}", "Physical_Name")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("{Udp:Class}", "Class", true)]
    [InlineData("{Udp:class}", "Class", true)]         // case-insensitive
    [InlineData("{Udp:Class|upper}", "Class", true)]   // pipe-aware
    [InlineData("{Class}", "Class", false)]            // own property, not UDP
    [InlineData("{Table.Class}", "Class", false)]      // related, not UDP
    [InlineData("{Udp:Other}", "Class", false)]
    [InlineData("no tokens", "Class", false)]
    [InlineData(null, "Class", false)]
    [InlineData("{Udp:Class}", null, false)]
    public void ReferencesOwnUdp_detects_only_matching_udp_tokens(string? template, string? udpName, bool expected)
    {
        NamingTemplateEngine.ReferencesOwnUdp(template, udpName).Should().Be(expected);
    }

    // ---- {Current}: the converging way to fold the target's own value in ----

    [Fact]
    public void Render_current_token_wraps_the_raw_target_value()
    {
        var related = Related(new Dictionary<string, string?> { ["Table.Physical_Name"] = "Table2" });

        string result = NamingTemplateEngine.Render(
            "{Table.Physical_Name}_{Current}", NoOwn, related, NoUdp, currentValue: "abc");

        result.Should().Be("Table2_abc");
    }

    [Fact]
    public void Render_current_token_is_a_fixed_point()
    {
        // THE reason {Current} exists: feeding the render's own output back in
        // must reproduce it byte for byte, otherwise the applier writes on every
        // heartbeat and the value grows without bound (the rule#1175 runaway).
        var related = Related(new Dictionary<string, string?> { ["Table.Physical_Name"] = "Table2" });
        const string template = "{Table.Physical_Name}_{Current}";

        string once = NamingTemplateEngine.Render(template, NoOwn, related, NoUdp, currentValue: "abc");
        string twice = NamingTemplateEngine.Render(template, NoOwn, related, NoUdp, currentValue: once);

        twice.Should().Be(once);
    }

    [Theory]
    [InlineData("abc", "X_abc_Y")]          // not shaped yet -> whole value is the seed
    [InlineData("X_abc_Y", "X_abc_Y")]      // already shaped -> middle is the seed
    [InlineData("X_Y", "X_X_Y_Y")]          // too short to strip -> whole value, then stable
    [InlineData("X_abc", "X_X_abc_Y")]      // prefix matches but suffix does not
    [InlineData("abc_Y", "X_abc_Y_Y")]      // suffix matches but prefix does not
    public void Render_current_token_seed_extraction(string current, string expected)
    {
        string result = NamingTemplateEngine.Render(
            "X_{Current}_Y", NoOwn, NoRelated, NoUdp, currentValue: current);

        result.Should().Be(expected);

        // Whatever the starting shape, ONE more render must settle it.
        NamingTemplateEngine.Render("X_{Current}_Y", NoOwn, NoRelated, NoUdp, currentValue: result)
            .Should().Be(result);
    }

    [Fact]
    public void Render_current_token_strips_case_sensitively()
    {
        // Ordinal, like the applier's "already correct" check: a case difference
        // means "not shaped", never a silent match.
        string result = NamingTemplateEngine.Render(
            "X_{Current}", NoOwn, NoRelated, NoUdp, currentValue: "x_abc");

        result.Should().Be("X_x_abc");
    }

    [Fact]
    public void Render_current_token_resolves_other_tokens_on_both_sides()
    {
        var own = Own(new Dictionary<string, string?> { ["Owner"] = "dbo" });
        var related = Related(new Dictionary<string, string?> { ["Table.Physical_Name"] = "ORDER" });
        var udp = Udp(new Dictionary<string, string?> { ["Suffix"] = "V2" });

        string result = NamingTemplateEngine.Render(
            "{Owner}_{Table.Physical_Name}_{Current}_{Udp:Suffix}", own, related, udp, currentValue: "ID");

        result.Should().Be("dbo_ORDER_ID_V2");
        NamingTemplateEngine.Render(
                "{Owner}_{Table.Physical_Name}_{Current}_{Udp:Suffix}", own, related, udp, currentValue: result)
            .Should().Be(result);
    }

    [Fact]
    public void Render_current_token_applies_an_idempotent_function_chain()
    {
        string result = NamingTemplateEngine.Render(
            "P_{Current|upper}", NoOwn, NoRelated, NoUdp, currentValue: "abc");

        result.Should().Be("P_ABC");
        NamingTemplateEngine.Render("P_{Current|upper}", NoOwn, NoRelated, NoUdp, currentValue: result)
            .Should().Be(result);
    }

    [Fact]
    public void Render_current_token_rejects_a_non_idempotent_chain()
    {
        // replace:a:aa grows on every pass, so the value would never settle -
        // the runaway sneaking back in through a pipe. Refuse, do not write.
        Action act = () => NamingTemplateEngine.Render(
            "{Current|replace:a:aa}", NoOwn, NoRelated, NoUdp, currentValue: "abc");

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*not idempotent*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Render_current_token_without_a_target_value_is_a_hard_error(string? current)
    {
        // {Current} RESHAPES an existing value; it never invents one.
        Action act = () => NamingTemplateEngine.Render(
            "P_{Current}", NoOwn, NoRelated, NoUdp, currentValue: current);

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*empty value*");
    }

    [Fact]
    public void Render_rejects_more_than_one_current_token()
    {
        // Ambiguous: which occurrence carries the old value? Refuse outright.
        Action act = () => NamingTemplateEngine.Render(
            "{Current}_{Current}", NoOwn, NoRelated, NoUdp, currentValue: "abc");

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*more than one*");
    }

    [Theory]
    [InlineData("{current}")]
    [InlineData("{CURRENT}")]
    [InlineData("{ Current }")]
    public void Render_current_token_is_case_and_space_insensitive(string token)
    {
        NamingTemplateEngine.Render("P_" + token, NoOwn, NoRelated, NoUdp, currentValue: "abc")
            .Should().Be("P_abc");
    }

    [Fact]
    public void Render_ignores_currentValue_when_no_current_token_is_present()
    {
        // Byte-identical to the pre-{Current} behaviour: an unrelated target
        // value must never leak into an ordinary template.
        var own = Own(new Dictionary<string, string?> { ["Physical_Name"] = "CUSTOMER" });

        NamingTemplateEngine.Render("PK_{Physical_Name}", own, NoRelated, NoUdp, currentValue: "junk")
            .Should().Be("PK_CUSTOMER");
    }

    [Theory]
    [InlineData("{Table.Physical_Name}_{Current}", true)]
    [InlineData("{current}", true)]
    [InlineData("{Current|upper}", true)]
    [InlineData("{Physical_Name}", false)]
    [InlineData("{Udp:Current}", false)]        // a UDP happening to be named Current
    [InlineData("{Table.Current}", false)]      // a related property named Current
    [InlineData("Current", false)]              // literal text, not a token
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsCurrentToken_detects_only_the_reserved_source(string? template, bool expected)
    {
        NamingTemplateEngine.ContainsCurrentToken(template).Should().Be(expected);
    }

    [Theory]
    [InlineData("{Current}", "OnlyIfEmpty", true)]
    [InlineData("{Current}", "onlyifempty", true)]
    [InlineData("{Current}", "Always", false)]
    [InlineData("{Physical_Name}", "OnlyIfEmpty", false)]
    [InlineData("{Current}", null, false)]      // unknown mode: ShouldWrite reports it
    public void CurrentTokenConflictsWithFillMode_flags_only_the_impossible_pair(
        string? template, string? fillMode, bool expected)
    {
        NamingTemplateEngine.CurrentTokenConflictsWithFillMode(template, fillMode).Should().Be(expected);
    }

    [Fact]
    public void ReferencesOwnProperty_does_not_flag_the_current_token()
    {
        // {Current} is the sanctioned alternative to self-reference, so the
        // runaway guard must let it through - including for a property that is
        // itself named Current, where the reserved source wins.
        NamingTemplateEngine.ReferencesOwnProperty("{Table.Physical_Name}_{Current}", "Physical_Name")
            .Should().BeFalse();
        NamingTemplateEngine.ReferencesOwnProperty("{Current}", "Current").Should().BeFalse();
    }

    [Fact]
    public void ReferencesOwnUdp_does_not_flag_the_current_token()
    {
        NamingTemplateEngine.ReferencesOwnUdp("{Current}", "Current").Should().BeFalse();
    }

    // ---- Escaping: '|' and ':' as DATA --------------------------------------
    //
    // '|' and ':' are the grammar's own separators, so before escaping existed a
    // pipe-delimited value could not be cut at all: "{Name|split:|:0}" reached
    // ApplyFunction as one argument and died with "expects 2 argument(s), got 1".
    // The backslash makes the separators expressible everywhere they can appear -
    // function arguments AND source names - while '}' stays structurally
    // impossible (it ends the token).

    [Theory]
    [InlineData(@"{Name|split:\|:0}", "A|B|C", "A")]
    [InlineData(@"{Name|split:\|:2}", "A|B|C", "C")]
    [InlineData(@"{Name|split:\::1}", "A:B:C", "B")]           // colon separator
    [InlineData(@"{Name|split:\|\|:1}", "A||B", "B")]          // multi-char, both escaped
    [InlineData(@"{Name|split:\\:1}", @"A\B", "B")]            // backslash separator
    [InlineData(@"{Name|replace:\|:_}", "A|B|C", "A_B_C")]
    [InlineData(@"{Name|replace:\::-}", "12:34", "12-34")]
    [InlineData(@"{Name|replace:\\:/}", @"A\B", "A/B")]
    [InlineData(@"{Name|replace:x:\|}", "AxB", "A|B")]         // escape in the REPLACEMENT too
    public void Render_escaped_separator_is_data_not_grammar(
        string template, string sourceValue, string expected)
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = sourceValue });

        NamingTemplateEngine.Render(template, own, NoRelated).Should().Be(expected);
    }

    [Fact]
    public void Render_escaped_pipe_does_not_break_the_chain_apart()
    {
        // The outer split has to skip "\|" without losing the REAL pipes around
        // it, otherwise the trailing function would be swallowed into the
        // separator argument.
        var own = Own(new Dictionary<string, string?> { ["Name"] = "  a|b|c  " });

        NamingTemplateEngine.Render(@"{Name|trim|split:\|:1|upper}", own, NoRelated).Should().Be("B");
    }

    [Fact]
    public void Render_escaped_backslash_is_a_literal_backslash_before_a_real_separator()
    {
        // "\\|" is an escaped backslash followed by a genuine separator - the
        // one case that forces the splitter to consume escape pairs whole.
        var own = Own(new Dictionary<string, string?> { ["Name"] = @"A\B" });

        NamingTemplateEngine.Render(@"{Name|replace:\\:_|lower}", own, NoRelated).Should().Be("a_b");
    }

    [Fact]
    public void Render_escape_reaches_source_names_too()
    {
        // Before this, a UDP whose name held a '|' was simply unreachable: the
        // outer split cut the name in half and the tail became a function.
        var udp = Udp(new Dictionary<string, string?> { ["A|B"] = "PIPED" });
        var own = Own(new Dictionary<string, string?> { ["Udp:Name"] = "NOT_A_UDP" });

        NamingTemplateEngine.Render(@"{Udp:A\|B}", NoOwn, NoRelated, udp).Should().Be("PIPED");
        // An escaped colon is NOT the {Udp:...} marker, so this reads the
        // property literally called "Udp:Name" - structure is decided on the
        // encoded text, exactly as ResolveSource does it.
        NamingTemplateEngine.Render(@"{Udp\:Name}", own, NoRelated, NoUdp).Should().Be("NOT_A_UDP");
    }

    [Theory]
    [InlineData(@"{Name|split:\a:0}")]        // unknown escape
    [InlineData(@"{Name|replace:\d:_}")]
    [InlineData(@"{Name|split:x\:0}")]        // escape swallows the arg separator -> wrong arity
    [InlineData(@"{\Name}")]                  // unknown escape in a source name
    public void Render_throws_on_a_bad_escape(string template)
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = "ORDER_ITEM" });

        Action act = () => NamingTemplateEngine.Render(template, own, NoRelated);

        act.Should().Throw<TemplateResolutionException>();
    }

    [Fact]
    public void Render_bad_escape_names_the_offending_sequence()
    {
        var own = Own(new Dictionary<string, string?> { ["Name"] = "ORDER_ITEM" });

        Action act = () => NamingTemplateEngine.Render(@"{Name|split:\a:0}", own, NoRelated);

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage(@"*unknown escape '\a'*");
    }

    [Fact]
    public void Render_dangling_backslash_is_a_hard_error_not_a_literal()
    {
        // A lone trailing '\' used to be a legal argument ("replace:a:\" wrote a
        // backslash). It is now refused LOUDLY rather than silently changing
        // meaning, and the message says what to write instead.
        var own = Own(new Dictionary<string, string?> { ["Name"] = "ORDER" });

        Action act = () => NamingTemplateEngine.Render(@"{Name|replace:a:\}", own, NoRelated);

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*dangling*");
    }

    [Fact]
    public void Render_attempt_to_escape_a_brace_is_told_that_braces_cannot_be_escaped()
    {
        // '}' ends the token before any escape logic runs, so "{Name|split:\}:0}"
        // reaches the engine as the token "Name|split:\" plus the literal text
        // ":0}". That surfaces as a dangling backslash - so the dangling message
        // is the one that has to mention braces, or the author gets told to write
        // "\\" for a mistake that was never about backslashes.
        var own = Own(new Dictionary<string, string?> { ["Name"] = "A}B" });

        Action act = () => NamingTemplateEngine.Render(@"{Name|split:\}:0}", own, NoRelated);

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*'}' cannot be escaped*");
    }

    [Fact]
    public void Render_numeric_argument_still_reports_an_integer_error_when_escaped()
    {
        // Arguments are decoded before the per-function checks, so an escape in a
        // numeric slot fails as a bad NUMBER (the author's actual mistake) rather
        // than leaking escape vocabulary into left/right/substr.
        var own = Own(new Dictionary<string, string?> { ["Name"] = "ORDER" });

        Action act = () => NamingTemplateEngine.Render(@"{Name|left:\:}", own, NoRelated);

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*non-negative integer*");
    }

    [Fact]
    public void Render_unescaped_separators_keep_their_old_meaning()
    {
        // Regression guard: escaping must not change a template that has none.
        var own = Own(new Dictionary<string, string?> { ["Name"] = "ORDER_ITEM_ID" });
        var udp = Udp(new Dictionary<string, string?> { ["A:B"] = "COLON_UDP" });

        NamingTemplateEngine.Render("{Name|split:_:1}", own, NoRelated).Should().Be("ITEM");
        NamingTemplateEngine.Render("{Name|replace:_:-}", own, NoRelated).Should().Be("ORDER-ITEM-ID");
        // A ':' inside a UDP name has always been legal and needs no escape.
        NamingTemplateEngine.Render("{Udp:A:B}", NoOwn, NoRelated, udp).Should().Be("COLON_UDP");
    }

    [Fact]
    public void Render_escape_survives_the_current_token_shape()
    {
        // {Current} takes the shaped path, which does its own pipe split - it has
        // to be escape-aware too, and the idempotency proof must still run.
        var related = Related(new Dictionary<string, string?> { ["Table.Physical_Name"] = "T" });

        NamingTemplateEngine
            .Render(@"{Table.Physical_Name}_{Current|split:\|:0}", NoOwn, related, NoUdp, "T_A|B")
            .Should().Be("T_A");
    }

    [Fact]
    public void Self_reference_guards_see_through_an_escaped_name()
    {
        // The runaway guard compares NAMES, so it has to decode the same way the
        // resolver does or an escaped self-reference would slip past it.
        NamingTemplateEngine.ReferencesOwnUdp(@"{Udp:A\|B|upper}", "A|B").Should().BeTrue();
        NamingTemplateEngine.ReferencesOwnUdp(@"{Udp:A\|B}", "A|C").Should().BeFalse();
        NamingTemplateEngine.ReferencesOwnProperty(@"{Udp\:Name}", "Udp:Name").Should().BeTrue();
    }

    [Fact]
    public void Self_reference_guards_stay_quiet_on_a_malformed_escape()
    {
        // These are pure predicates used to REFUSE a rule; a broken template is
        // not their error to report - Render throws with the precise reason a
        // moment later - so they must not blow up on one.
        NamingTemplateEngine.ReferencesOwnProperty(@"{\Name}", "Name").Should().BeFalse();
        NamingTemplateEngine.ReferencesOwnUdp(@"{Udp:A\zB}", "A\\zB").Should().BeFalse();
        NamingTemplateEngine.ContainsCurrentToken(@"{Curr\ent}").Should().BeFalse();
    }

    // ---- Live rules 1180/1181 (MetaRepoZeynep config 1012) ----------------
    // The whole production shape at once: a {Udp:...} source, an escaped pipe
    // separator, and pieces that carry whitespace. Each ingredient was covered
    // separately; the combination that actually shipped was not.

    private static Dictionary<string, string?> ApplicationUdp(string value) =>
        new() { ["Application"] = value };

    [Theory]
    [InlineData(@"{Udp:Application|split:\|:0}", "UYG051 ")]
    [InlineData(@"{Udp:Application|split:\|:1}", " Kurumsal Internet-Mobil Bankaciligi")]
    public void Splitting_on_an_escaped_pipe_keeps_the_whitespace_around_the_separator(string template, string expected)
    {
        // The separator is used VERBATIM, so "A | B" splits into "A " and " B".
        // Rules 1180/1181 are written without a trailing |trim, so this IS what
        // they write today. Asserted rather than fixed in code: silently trimming
        // would hide the authoring error and make the stored value differ from the
        // declared template. The correction is an admin-DB data change.
        NamingTemplateEngine
            .Render(template, NoOwn, NoRelated, Udp(ApplicationUdp("UYG051 | Kurumsal Internet-Mobil Bankaciligi")), "")
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(@"{Udp:Application|split:\|:0|trim}", "UYG051")]
    [InlineData(@"{Udp:Application|split:\|:1|trim}", "Kurumsal Internet-Mobil Bankaciligi")]
    public void Trim_after_split_produces_the_values_the_rules_intend(string template, string expected)
    {
        // Order matters: |trim must come AFTER |split. Trimming the whole value
        // first ({Udp:Application|trim|split:\|:1}) still leaves the leading space
        // on the second piece.
        NamingTemplateEngine
            .Render(template, NoOwn, NoRelated, Udp(ApplicationUdp("UYG051 | Kurumsal Internet-Mobil Bankaciligi")), "")
            .Should().Be(expected);
    }

    [Fact]
    public void Trim_before_split_does_not_clean_the_inner_pieces()
    {
        NamingTemplateEngine
            .Render(@"{Udp:Application|trim|split:\|:1}", NoOwn, NoRelated, Udp(ApplicationUdp("UYG051 | Kurumsal")), "")
            .Should().Be(" Kurumsal");
    }

    [Fact]
    public void Rule_1181_aborts_when_the_source_value_carries_no_separator()
    {
        // Index 1 of a one-piece split is out of range -> empty -> the NO-FALLBACK
        // empty-chain check throws. Correct behaviour, but it means the MODEL
        // applier must catch it into a logged skip and never let it reach the
        // heartbeat thread.
        Action act = () => NamingTemplateEngine.Render(
            @"{Udp:Application|split:\|:1}", NoOwn, NoRelated, Udp(ApplicationUdp("UYG051")), "");

        act.Should().Throw<TemplateResolutionException>()
            .WithMessage("*empty value*");
    }
}
