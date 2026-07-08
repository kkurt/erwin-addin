using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using Forms = EliteSoft.Erwin.AddIn.Forms;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Pure-matcher coverage for the admin "Datatype Library" whitelist
/// (<see cref="AllowedDatatypeService.IsDatatypeAllowed"/>). Rules: empty whitelist =
/// no restriction; base token matched case-insensitively; a non-parameterized entry
/// permits the base ONLY without a length; a parameterized entry REQUIRES a length
/// (bare 'varchar2' rejected when 'varchar2' is parameterized - user decision
/// 2026-07-05).
/// </summary>
public class AllowedDatatypeMatcherTests
{
    private static AllowedDatatypeEntry T(string name, bool param = false) =>
        new() { Datatype = name, IsParameterized = param };

    private static List<AllowedDatatypeEntry> Set(params AllowedDatatypeEntry[] e) => new(e);

    // ---------- empty whitelist = no restriction ----------

    [Theory]
    [InlineData("int")]
    [InlineData("varchar(50)")]
    [InlineData("anything")]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_whitelist_allows_everything(string? value)
    {
        AllowedDatatypeService.IsDatatypeAllowed(value, Set()).Should().BeTrue();
        AllowedDatatypeService.IsDatatypeAllowed(value, null).Should().BeTrue();
    }

    // ---------- non-parameterized entry (e.g. "int") ----------

    [Theory]
    [InlineData("int", true)]
    [InlineData("INT", true)]          // case-insensitive base
    [InlineData("  int  ", true)]      // parser trims
    [InlineData("int(5)", false)]      // non-param must NOT carry a length
    [InlineData("bigint", false)]      // different base
    [InlineData("varchar(50)", false)]
    public void NonParameterized_int_only(string value, bool expected)
    {
        var allowed = Set(T("int"));
        AllowedDatatypeService.IsDatatypeAllowed(value, allowed).Should().Be(expected);
    }

    // ---------- parameterized entry (e.g. "nvarchar") ----------

    [Theory]
    [InlineData("nvarchar(50)", true)]
    [InlineData("nvarchar(4000)", true)]
    [InlineData("NVARCHAR(200)", true)]   // case-insensitive
    [InlineData("nvarchar", false)]       // param base REQUIRES a length - bare rejected (2026-07-05)
    [InlineData("int", false)]            // different base
    [InlineData("varchar(50)", false)]    // different base (varchar != nvarchar)
    public void Parameterized_nvarchar(string value, bool expected)
    {
        var allowed = Set(T("nvarchar", param: true));
        AllowedDatatypeService.IsDatatypeAllowed(value, allowed).Should().Be(expected);
    }

    [Theory]
    [InlineData("Numeric(10,2)", true)]   // multi-arg length under a parameterized entry
    [InlineData("Numeric(18)", true)]
    [InlineData("numeric", false)]        // param base REQUIRES a length - bare rejected (2026-07-05)
    [InlineData("Number(10,2)", false)]   // Oracle 'Number' != 'Numeric'
    public void Parameterized_numeric_with_precision(string value, bool expected)
    {
        AllowedDatatypeService.IsDatatypeAllowed(value, Set(T("Numeric", param: true))).Should().Be(expected);
    }

    // ---------- mixed whitelist (the user's likely real config) ----------

    [Fact]
    public void Mixed_whitelist_int_and_nvarchar()
    {
        var allowed = Set(T("int"), T("nvarchar", param: true), T("bit"), T("DateTime"));

        AllowedDatatypeService.IsDatatypeAllowed("int", allowed).Should().BeTrue();
        AllowedDatatypeService.IsDatatypeAllowed("nvarchar(255)", allowed).Should().BeTrue();
        AllowedDatatypeService.IsDatatypeAllowed("bit", allowed).Should().BeTrue();
        AllowedDatatypeService.IsDatatypeAllowed("datetime", allowed).Should().BeTrue();

        AllowedDatatypeService.IsDatatypeAllowed("varchar(50)", allowed).Should().BeFalse();
        AllowedDatatypeService.IsDatatypeAllowed("float", allowed).Should().BeFalse();
        AllowedDatatypeService.IsDatatypeAllowed("char(18)", allowed).Should().BeFalse();
        AllowedDatatypeService.IsDatatypeAllowed("binary()", allowed).Should().BeFalse();
        AllowedDatatypeService.IsDatatypeAllowed("int(5)", allowed).Should().BeFalse(); // int is non-param
        AllowedDatatypeService.IsDatatypeAllowed("nvarchar", allowed).Should().BeFalse(); // param base needs a length
    }

    // ---------- unclassifiable input is not blocked ----------

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Unclassifiable_value_is_allowed(string? value)
    {
        // A non-empty whitelist must not block an empty/blank type (cannot classify) -
        // erwin transiently surfaces "" mid-edit; blocking it would fight the editor.
        AllowedDatatypeService.IsDatatypeAllowed(value, Set(T("int"))).Should().BeTrue();
    }

    // ---------- SeedForTesting reaches the instance matcher ----------

    // ---------- GetFallbackDatatype: the type a disallowed value is forced to ----------

    [Fact]
    public void GetFallbackDatatype_prefers_first_complete_nonparameterized()
    {
        // A parameterized entry first, then a complete non-param one: the non-param wins
        // because its base token is a valid standalone Physical_Data_Type ("int").
        AllowedDatatypeService.Instance.SeedForTesting(new[] { T("nvarchar", param: true), T("int") });
        try
        {
            AllowedDatatypeService.Instance.GetFallbackDatatype().Should().Be("int");
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(System.Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_single_allowed_type_is_used()
    {
        AllowedDatatypeService.Instance.SeedForTesting(new[] { T("int") });
        try
        {
            AllowedDatatypeService.Instance.GetFallbackDatatype().Should().Be("int");
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(System.Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_all_parameterized_synthesizes_minimal_length()
    {
        // No non-parameterized type to fall back on: a bare "nvarchar" may be rejected by erwin, so
        // the fallback synthesizes a minimal valid length. Which base wins depends on the load
        // query's ORDER BY dl.DATATYPE (not SeedForTesting's insertion order), so assert either -
        // and assert the synthesized token round-trips through the matcher (no revert loop).
        AllowedDatatypeService.Instance.SeedForTesting(new[] { T("nvarchar", param: true), T("Numeric", param: true) });
        try
        {
            var fb = AllowedDatatypeService.Instance.GetFallbackDatatype();
            fb.Should().BeOneOf("nvarchar(1)", "Numeric(1)");
            AllowedDatatypeService.Instance.IsAllowed(fb).Should().BeTrue();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(System.Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_null_when_no_restriction()
    {
        AllowedDatatypeService.Instance.SeedForTesting(System.Array.Empty<AllowedDatatypeEntry>());
        AllowedDatatypeService.Instance.GetFallbackDatatype().Should().BeNull();
    }

    [Fact]
    public void SeedForTesting_drives_instance_IsAllowed()
    {
        AllowedDatatypeService.Instance.SeedForTesting(new[] { T("int") });
        try
        {
            AllowedDatatypeService.Instance.HasRestriction.Should().BeTrue();
            AllowedDatatypeService.Instance.IsAllowed("int").Should().BeTrue();
            AllowedDatatypeService.Instance.IsAllowed("varchar(10)").Should().BeFalse();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(System.Array.Empty<AllowedDatatypeEntry>()); }

        // Empty seed -> no restriction.
        AllowedDatatypeService.Instance.HasRestriction.Should().BeFalse();
        AllowedDatatypeService.Instance.IsAllowed("anything").Should().BeTrue();
    }
}

/// <summary>
/// Pure-composition coverage for the AllowedDatatypePickerForm statics: the picker
/// composes <c>base(param)</c> from the user's combo pick + parameter text, validates
/// the parameter shape (n or n,m), and prefills from the attempted type's parameter.
/// The WinForms chrome is not under test - only the value logic the enforcement
/// writes into Physical_Data_Type.
/// </summary>
public class AllowedDatatypePickerLogicTests
{
    [Theory]
    [InlineData("nvarchar", "50", "nvarchar(50)")]
    [InlineData("NUMBER", "10,2", "NUMBER(10,2)")]
    [InlineData("NUMBER", " 10 , 2 ", "NUMBER(10,2)")]   // spaces stripped inside param
    [InlineData("int", "", "int")]                        // empty param -> bare base
    [InlineData("int", null, "int")]
    [InlineData(" date ", "", "date")]                    // base trimmed
    [InlineData("", "50", "")]                            // no base -> nothing
    public void Compose_builds_physical_datatype(string baseToken, string param, string expected)
    {
        Forms.AllowedDatatypePickerForm.Compose(baseToken, param).Should().Be(expected);
    }

    [Theory]
    [InlineData("", true)]          // optional
    [InlineData(null, true)]
    [InlineData("18", true)]
    [InlineData("10,2", true)]
    [InlineData(" 10 , 2 ", true)]
    [InlineData("abc", false)]
    [InlineData("10,", false)]
    [InlineData(",2", false)]
    [InlineData("10,2,3", false)]
    [InlineData("(18)", false)]
    public void IsValidParameter_accepts_n_or_n_comma_m(string param, bool expected)
    {
        Forms.AllowedDatatypePickerForm.IsValidParameter(param).Should().Be(expected);
    }

    [Theory]
    [InlineData("char(18)", "18")]
    [InlineData("NUMBER(10,2)", "10,2")]
    [InlineData("date", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ExtractParameter_pulls_parenthesized_part(string datatype, string expected)
    {
        Forms.AllowedDatatypePickerForm.ExtractParameter(datatype).Should().Be(expected);
    }

    // ---------- ValidateComposition: the picker's accept/reject decision ----------
    // 2026-07-07: the picker now runs the admin naming/regex rules against the COMPOSED
    // datatype before committing, so a rule-violating value (e.g. nvarchar(4200) when a
    // length <= 4000 rule exists) can never leave the dialog - the gap that let the Model
    // Explorer path bypass rule validation entirely.

    private static AllowedDatatypeEntry Entry(string name, bool param = false) =>
        new() { Datatype = name, IsParameterized = param };

    [Fact]
    public void ValidateComposition_accepts_non_parameterized_without_validator()
    {
        Forms.AllowedDatatypePickerForm
            .ValidateComposition(Entry("int"), "", null)
            .Should().BeNull();
    }

    [Fact]
    public void ValidateComposition_requires_length_for_parameterized()
    {
        Forms.AllowedDatatypePickerForm
            .ValidateComposition(Entry("varchar2", param: true), "", null)
            .Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ValidateComposition_rejects_bad_length_syntax()
    {
        Forms.AllowedDatatypePickerForm
            .ValidateComposition(Entry("varchar2", param: true), "abc", null)
            .Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ValidateComposition_accepts_valid_length_without_validator()
    {
        Forms.AllowedDatatypePickerForm
            .ValidateComposition(Entry("varchar2", param: true), "4000", null)
            .Should().BeNull();
    }

    [Fact]
    public void ValidateComposition_surfaces_rule_violation_on_composed_value()
    {
        // Emulates an admin "NVARCHAR length must be <= 4000" rule: the validator sees the
        // COMPOSED token and rejects 4200 - the exact value the user reported slipping through.
        string? Validator(string composed) =>
            composed == "nvarchar(4200)" ? "NVARCHAR length must be <= 4000." : null;

        Forms.AllowedDatatypePickerForm
            .ValidateComposition(Entry("nvarchar", param: true), "4200", Validator)
            .Should().Be("NVARCHAR length must be <= 4000.");
    }

    [Fact]
    public void ValidateComposition_accepts_rule_valid_composed_value()
    {
        string? Validator(string composed) =>
            composed == "nvarchar(4200)" ? "too long" : null;

        Forms.AllowedDatatypePickerForm
            .ValidateComposition(Entry("nvarchar", param: true), "4000", Validator)
            .Should().BeNull();
    }

    [Fact]
    public void ValidateComposition_skips_rule_validator_when_length_syntax_fails()
    {
        // Param-syntax gate runs FIRST: an invalid/empty length short-circuits before the rule
        // validator is consulted, so a malformed token is never rule-validated.
        bool validatorCalled = false;
        string? Validator(string composed) { validatorCalled = true; return null; }

        Forms.AllowedDatatypePickerForm
            .ValidateComposition(Entry("nvarchar", param: true), "", Validator)
            .Should().NotBeNullOrEmpty();
        validatorCalled.Should().BeFalse();
    }

    [Fact]
    public void ValidateComposition_applies_rule_validator_to_non_parameterized_type()
    {
        // The rule gate is not limited to parameterized types: a bare base can also violate
        // an admin datatype rule.
        string? Validator(string composed) => composed == "text" ? "TEXT is not permitted." : null;

        Forms.AllowedDatatypePickerForm
            .ValidateComposition(Entry("text"), "", Validator)
            .Should().Be("TEXT is not permitted.");
    }
}
