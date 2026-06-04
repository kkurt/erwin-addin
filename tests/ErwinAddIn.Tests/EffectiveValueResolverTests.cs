using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Unit coverage for the PURE (DB-free) value parsers behind the two-level
/// effective-value resolver (model CONFIG_PROPERTY -> corporate CORPORATE_PROPERTY
/// -> code default), added 2026-06-04. These turn the raw cascade string into
/// bool / int / enum. The DB cascade itself (GetEffective) needs a live
/// RepoDbContext and is exercised end-to-end against the admin repo when the
/// add-in attaches to a Mart model (verified live: config 1010 GLOSSARY_LOAD_INTERVAL
/// honours the admin's 15, APPLY_UDP_CHANGES_SILENTLY cascades from corporate, etc.).
/// </summary>
public class EffectiveValueResolverTests
{
    // ---------------- ParseEffectiveBool ----------------

    [Theory]
    // Spec format ("True"/"False", case-insensitive via bool.TryParse).
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("False", false)]
    [InlineData("false", false)]
    // Whitespace is trimmed.
    [InlineData("  True  ", true)]
    // Backward-compat legacy truthiness from the old readers (Yes / 1 / No / 0).
    [InlineData("Yes", true)]
    [InlineData("yes", true)]
    [InlineData("1", true)]
    [InlineData("No", false)]
    [InlineData("0", false)]
    public void ParseEffectiveBool_parses_known_values(string value, bool expected)
    {
        // default is the OPPOSITE of expected so a no-op (returning default) would fail.
        ConfigContextService.ParseEffectiveBool(value, !expected).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("Trueish")]
    public void ParseEffectiveBool_missing_or_unparseable_returns_default(string? value)
    {
        // The cascade returns null when neither level set the key -> default applies.
        ConfigContextService.ParseEffectiveBool(value!, defaultValue: false).Should().BeFalse();
        ConfigContextService.ParseEffectiveBool(value!, defaultValue: true).Should().BeTrue();
    }

    // ---------------- ParseEffectiveInt ----------------

    [Theory]
    [InlineData("5", 5)]
    [InlineData("15", 15)]
    [InlineData("  7 ", 7)]
    [InlineData("0", 0)]
    [InlineData("-3", -3)]
    public void ParseEffectiveInt_parses_integers(string value, int expected)
    {
        ConfigContextService.ParseEffectiveInt(value, defaultValue: 999).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("5.5")]
    public void ParseEffectiveInt_missing_or_unparseable_returns_default(string? value)
    {
        ConfigContextService.ParseEffectiveInt(value!, defaultValue: 5).Should().Be(5);
    }

    // ---------------- ParseEffectiveEnum (GlossaryRequiredOption) ----------------

    [Theory]
    [InlineData("REQUIRED", GlossaryRequiredOption.REQUIRED)]
    [InlineData("required", GlossaryRequiredOption.REQUIRED)]
    [InlineData("OPTIONAL_WARNING", GlossaryRequiredOption.OPTIONAL_WARNING)]
    [InlineData("optional_warning", GlossaryRequiredOption.OPTIONAL_WARNING)]
    [InlineData("OPTIONAL_SILENT", GlossaryRequiredOption.OPTIONAL_SILENT)]
    [InlineData("  REQUIRED  ", GlossaryRequiredOption.REQUIRED)]
    public void ParseEffectiveEnum_parses_known_names(string value, GlossaryRequiredOption expected)
    {
        // default deliberately differs from expected so a fallthrough would fail.
        var fallback = expected == GlossaryRequiredOption.REQUIRED
            ? GlossaryRequiredOption.OPTIONAL_SILENT
            : GlossaryRequiredOption.REQUIRED;
        ConfigContextService.ParseEffectiveEnum(value, fallback).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("REQUIREDISH")]
    [InlineData("99")]            // out-of-range numeric -> IsDefined guard rejects it
    public void ParseEffectiveEnum_missing_or_unparseable_returns_default(string? value)
    {
        ConfigContextService.ParseEffectiveEnum(value!, GlossaryRequiredOption.OPTIONAL_SILENT)
            .Should().Be(GlossaryRequiredOption.OPTIONAL_SILENT);
    }
}
