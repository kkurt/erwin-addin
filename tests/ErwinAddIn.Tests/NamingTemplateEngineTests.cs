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
/// left/right/substr/replace), literal-text preservation, the NO-FALLBACK
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
}
