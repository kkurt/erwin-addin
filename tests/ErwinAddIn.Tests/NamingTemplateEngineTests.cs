using System;
using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for <see cref="NamingTemplateEngine"/>, the pure renderer
/// behind the "Template" naming rule type: token grammar ({Own} and
/// {Alias.Prop}), literal-text preservation, the NO-FALLBACK contract
/// (an unresolvable token aborts the whole render), and the TEMPLATE_FILL_MODE
/// write decision. SCAPI navigation lives in the runtime applier and is not
/// exercised here on purpose - this guards the grammar/semantics in isolation.
/// </summary>
public class NamingTemplateEngineTests
{
    // A reader that fails every lookup; handy for "no tokens" assertions.
    private static string? NoOwn(string code) => throw new InvalidOperationException($"unexpected own read '{code}'");
    private static string? NoRelated(string alias, string code) => throw new InvalidOperationException($"unexpected related read '{alias}.{code}'");

    private static Func<string, string?> Own(Dictionary<string, string?> map) =>
        code => map.TryGetValue(code, out var v) ? v : null;

    private static Func<string, string, string?> Related(Dictionary<string, string?> map) =>
        (alias, code) => map.TryGetValue($"{alias}.{code}", out var v) ? v : null;

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
}
