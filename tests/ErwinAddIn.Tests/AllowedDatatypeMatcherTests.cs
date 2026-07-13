using System;
using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using Forms = EliteSoft.Erwin.AddIn.Forms;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Pure-matcher coverage for the admin "Datatype Library" whitelist
/// (<see cref="AllowedDatatypeService.ValidateDatatype"/> / <see cref="AllowedDatatypeService.IsDatatypeAllowed"/>).
/// 2026-07-08 model: each entry has PARAMETRIZATION_TYPE (None/Standard/Regex) + ALLOW_NON_PARAMETRIZED
/// (+ REGEX_PATTERN / REGEX_ERROR for Regex). Rules: empty whitelist = no restriction; base matched
/// case-insensitively; None = bare-only (a parameter is invalid); Standard/Regex require a parameter
/// unless the bare form is allowed; Regex validates the parameter against REGEX_PATTERN (REGEX_ERROR on
/// failure). Back-compat migration: old IS_PARAMETERIZED=1 -> Standard + allowBare=off (param required);
/// =0 -> None (bare-only) - so the factories below reproduce today's behaviour.
/// </summary>
public class AllowedDatatypeMatcherTests
{
    // Bare-only type (migrated from IS_PARAMETERIZED=0).
    private static AllowedDatatypeEntry None(string name) =>
        new() { Datatype = name, ParametrizationType = DatatypeParametrization.None };

    // Standard length/precision type; allowBare=false reproduces the old IS_PARAMETERIZED=1 "param required".
    private static AllowedDatatypeEntry Std(string name, bool allowBare = false) =>
        new() { Datatype = name, ParametrizationType = DatatypeParametrization.Standard, AllowNonParametrized = allowBare };

    // Regex-validated parameter type.
    private static AllowedDatatypeEntry Rgx(string name, string pattern, string error = null, bool allowBare = false) =>
        new()
        {
            Datatype = name,
            ParametrizationType = DatatypeParametrization.Regex,
            AllowNonParametrized = allowBare,
            RegexPattern = pattern,
            RegexError = error,
        };

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

    // ---------- None (bare-only, e.g. "int") ----------

    [Theory]
    [InlineData("int", true)]
    [InlineData("INT", true)]          // case-insensitive base
    [InlineData("  int  ", true)]      // parser trims
    [InlineData("int(5)", false)]      // None must NOT carry a parameter
    [InlineData("bigint", false)]      // different base (not whitelisted)
    [InlineData("varchar(50)", false)]
    public void None_int_only(string value, bool expected)
    {
        AllowedDatatypeService.IsDatatypeAllowed(value, Set(None("int"))).Should().Be(expected);
    }

    // ---------- Standard, param required (migrated IS_PARAMETERIZED=1) ----------

    [Theory]
    [InlineData("nvarchar(50)", true)]
    [InlineData("nvarchar(4000)", true)]
    [InlineData("NVARCHAR(200)", true)]   // case-insensitive
    [InlineData("nvarchar", false)]       // Standard+allowBare=off REQUIRES a parameter
    [InlineData("int", false)]            // different base
    [InlineData("varchar(50)", false)]    // different base (varchar != nvarchar)
    public void Standard_required_nvarchar(string value, bool expected)
    {
        AllowedDatatypeService.IsDatatypeAllowed(value, Set(Std("nvarchar"))).Should().Be(expected);
    }

    // ---------- Standard, bare allowed (ALLOW_NON_PARAMETRIZED=1) ----------

    [Theory]
    [InlineData("decimal", true)]         // bare accepted
    [InlineData("decimal(10,2)", true)]   // param accepted
    [InlineData("DECIMAL", true)]
    public void Standard_bare_allowed_decimal(string value, bool expected)
    {
        AllowedDatatypeService.IsDatatypeAllowed(value, Set(Std("decimal", allowBare: true))).Should().Be(expected);
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
        AllowedDatatypeService.IsDatatypeAllowed(value, Set(None("int"))).Should().BeTrue();
    }

    // ---------- mixed config: the full acceptance matrix the task asks for ----------

    [Fact]
    public void Mixed_config_covers_all_parametrization_rules()
    {
        // int  = None (bare-only); nvarchar = Standard required; decimal = Standard bare-allowed;
        // varchar2 = Regex (param must be 1-3 digits) with a custom REGEX_ERROR.
        var allowed = Set(
            None("int"),
            Std("nvarchar"),
            Std("decimal", allowBare: true),
            Rgx("varchar2", @"^\d{1,3}$", error: "varchar2 length must be 1-999."));

        // whitelist-out -> rejected
        AllowedDatatypeService.ValidateDatatype("float", allowed).IsValid.Should().BeFalse();
        AllowedDatatypeService.ValidateDatatype("varchar(50)", allowed).IsValid.Should().BeFalse();

        // None: bare ok, param rejected with a message
        AllowedDatatypeService.ValidateDatatype("int", allowed).IsValid.Should().BeTrue();
        var noneParam = AllowedDatatypeService.ValidateDatatype("int(5)", allowed);
        noneParam.IsValid.Should().BeFalse();
        noneParam.Message.Should().Contain("does not take a parameter");

        // Standard required: bare rejected, param accepted
        AllowedDatatypeService.ValidateDatatype("nvarchar", allowed).IsValid.Should().BeFalse();
        AllowedDatatypeService.ValidateDatatype("nvarchar(255)", allowed).IsValid.Should().BeTrue();

        // Standard bare-allowed: both accepted
        AllowedDatatypeService.ValidateDatatype("decimal", allowed).IsValid.Should().BeTrue();
        AllowedDatatypeService.ValidateDatatype("decimal(10,2)", allowed).IsValid.Should().BeTrue();

        // Regex: matching param accepted; non-matching rejected WITH the custom REGEX_ERROR
        AllowedDatatypeService.ValidateDatatype("varchar2(50)", allowed).IsValid.Should().BeTrue();
        var rgxBad = AllowedDatatypeService.ValidateDatatype("varchar2(5000)", allowed); // 4 digits -> fails ^\d{1,3}$
        rgxBad.IsValid.Should().BeFalse();
        rgxBad.Message.Should().Be("varchar2 length must be 1-999.");
        // Regex + allowBare=off: bare rejected (requires a parameter)
        AllowedDatatypeService.ValidateDatatype("varchar2", allowed).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Regex_generic_message_when_regex_error_blank()
    {
        var allowed = Set(Rgx("varchar2", @"^\d{1,3}$", error: null));
        var r = AllowedDatatypeService.ValidateDatatype("varchar2(5000)", allowed);
        r.IsValid.Should().BeFalse();
        r.Message.Should().NotBeNullOrEmpty();
        r.Message.Should().Contain("varchar2");
    }

    [Fact]
    public void Regex_bare_allowed_accepts_bare()
    {
        var allowed = Set(Rgx("varchar2", @"^\d{1,3}$", error: "bad", allowBare: true));
        AllowedDatatypeService.ValidateDatatype("varchar2", allowed).IsValid.Should().BeTrue();      // bare ok
        AllowedDatatypeService.ValidateDatatype("varchar2(50)", allowed).IsValid.Should().BeTrue();  // param ok
        AllowedDatatypeService.ValidateDatatype("varchar2(5000)", allowed).IsValid.Should().BeFalse(); // param fails regex
    }

    // ---------- GetFallbackDatatype: the type a disallowed value is forced to ----------

    [Fact]
    public void GetFallbackDatatype_prefers_a_bare_usable_type()
    {
        // A param-required Standard entry first, then a bare-usable None ("int"): the bare-usable
        // wins because its base token is a valid standalone Physical_Data_Type.
        AllowedDatatypeService.Instance.SeedForTesting(new[] { Std("nvarchar"), None("int") });
        try
        {
            AllowedDatatypeService.Instance.GetFallbackDatatype().Should().Be("int");
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_standard_bare_allowed_is_bare_usable()
    {
        AllowedDatatypeService.Instance.SeedForTesting(new[] { Std("nvarchar"), Std("decimal", allowBare: true) });
        try
        {
            AllowedDatatypeService.Instance.GetFallbackDatatype().Should().Be("decimal");
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_all_param_required_synthesizes_minimal_length()
    {
        // No bare-usable type: a bare "nvarchar" may be rejected by erwin, so the fallback
        // synthesizes a minimal length and it must round-trip through the matcher (no revert loop).
        AllowedDatatypeService.Instance.SeedForTesting(new[] { Std("nvarchar"), Std("Numeric") });
        try
        {
            var fb = AllowedDatatypeService.Instance.GetFallbackDatatype();
            fb.Should().BeOneOf("nvarchar(1)", "Numeric(1)");
            AllowedDatatypeService.Instance.IsAllowed(fb).Should().BeTrue();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_null_when_no_restriction()
    {
        AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>());
        AllowedDatatypeService.Instance.GetFallbackDatatype().Should().BeNull();
    }

    [Fact]
    public void SeedForTesting_drives_instance_IsAllowed()
    {
        AllowedDatatypeService.Instance.SeedForTesting(new[] { None("int") });
        try
        {
            AllowedDatatypeService.Instance.HasRestriction.Should().BeTrue();
            AllowedDatatypeService.Instance.IsAllowed("int").Should().BeTrue();
            AllowedDatatypeService.Instance.IsAllowed("varchar(10)").Should().BeFalse();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>()); }

        // Empty seed -> no restriction.
        AllowedDatatypeService.Instance.HasRestriction.Should().BeFalse();
        AllowedDatatypeService.Instance.IsAllowed("anything").Should().BeTrue();
    }
}

/// <summary>
/// Pure-composition coverage for the AllowedDatatypePickerForm statics: the picker composes
/// <c>base(param)</c> from the user's combo pick + parameter text, and (2026-07-08) validates the
/// composition against the selected entry's parametrization rule (None/Standard/Regex) plus any
/// admin naming rule. The WinForms chrome is not under test - only the value logic.
/// </summary>
public class AllowedDatatypePickerLogicTests
{
    [Theory]
    [InlineData("nvarchar", "50", "nvarchar(50)")]
    [InlineData("NUMBER", "10,2", "NUMBER(10,2)")]
    [InlineData("NUMBER", " 10 , 2 ", "NUMBER(10,2)")]   // whitespace AROUND the comma collapsed
    [InlineData("int", "", "int")]                        // empty param -> bare base
    [InlineData("int", null, "int")]
    [InlineData(" date ", "", "date")]                    // base trimmed
    [InlineData("", "50", "")]                            // no base -> nothing
    // 2026-07-10: significant internal whitespace is PRESERVED (was wrongly stripped, breaking the
    // Oracle "VARCHAR2(55 CHAR)" regex whose admin pattern requires the space).
    [InlineData("VARCHAR2", "55 CHAR", "VARCHAR2(55 CHAR)")]
    [InlineData("VARCHAR2", "  55   CHAR  ", "VARCHAR2(55   CHAR)")] // only leading/trailing trimmed
    [InlineData("VARCHAR2", "10 BYTE", "VARCHAR2(10 BYTE)")]
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
    // Applies the selected entry's parametrization rule (via AllowedDatatypeService.ValidateAgainstEntry)
    // then the optional admin naming-rule validator.

    private static AllowedDatatypeEntry None(string name) =>
        new() { Datatype = name, ParametrizationType = DatatypeParametrization.None };
    private static AllowedDatatypeEntry Std(string name, bool allowBare = false) =>
        new() { Datatype = name, ParametrizationType = DatatypeParametrization.Standard, AllowNonParametrized = allowBare };
    private static AllowedDatatypeEntry Rgx(string name, string pattern, string error = null, bool allowBare = false) =>
        new() { Datatype = name, ParametrizationType = DatatypeParametrization.Regex, AllowNonParametrized = allowBare, RegexPattern = pattern, RegexError = error };

    [Fact]
    public void ValidateComposition_accepts_none_without_param()
    {
        Forms.AllowedDatatypePickerForm.ValidateComposition(None("int"), "", null).Should().BeNull();
    }

    [Fact]
    public void ValidateComposition_none_rejects_param()
    {
        Forms.AllowedDatatypePickerForm
            .ValidateComposition(None("int"), "5", null)
            .Should().Contain("does not take a parameter");
    }

    [Fact]
    public void ValidateComposition_standard_required_rejects_bare()
    {
        Forms.AllowedDatatypePickerForm
            .ValidateComposition(Std("varchar2"), "", null)
            .Should().Contain("requires a parameter");
    }

    [Fact]
    public void ValidateComposition_standard_accepts_any_param_format()
    {
        // Per spec, STANDARD does NOT format-check the parameter (DB/erwin owns the format).
        Forms.AllowedDatatypePickerForm.ValidateComposition(Std("varchar2"), "4000", null).Should().BeNull();
        Forms.AllowedDatatypePickerForm.ValidateComposition(Std("varchar2"), "anything", null).Should().BeNull();
    }

    [Fact]
    public void ValidateComposition_standard_bare_allowed_accepts_bare()
    {
        Forms.AllowedDatatypePickerForm.ValidateComposition(Std("decimal", allowBare: true), "", null).Should().BeNull();
    }

    [Fact]
    public void ValidateComposition_regex_surfaces_regex_error_on_mismatch()
    {
        var entry = Rgx("varchar2", @"^\d{1,3}$", error: "varchar2 length must be 1-999.");
        Forms.AllowedDatatypePickerForm
            .ValidateComposition(entry, "5000", null)
            .Should().Be("varchar2 length must be 1-999.");
    }

    [Fact]
    public void ValidateComposition_regex_accepts_matching_param()
    {
        var entry = Rgx("varchar2", @"^\d{1,3}$", error: "bad");
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "50", null).Should().BeNull();
    }

    [Fact]
    public void ValidateComposition_surfaces_naming_rule_violation_on_composed_value()
    {
        // The whitelist rule passes (Standard + param), then the admin naming-rule validator sees
        // the COMPOSED token and rejects it - the Model Explorer gap closer.
        string? Validator(string composed) =>
            composed == "nvarchar(4200)" ? "NVARCHAR length must be <= 4000." : null;

        Forms.AllowedDatatypePickerForm
            .ValidateComposition(Std("nvarchar"), "4200", Validator)
            .Should().Be("NVARCHAR length must be <= 4000.");
    }

    [Fact]
    public void ValidateComposition_skips_naming_validator_when_whitelist_rule_fails()
    {
        // Whitelist rule runs FIRST: a Standard-required type with no param short-circuits before
        // the naming validator is consulted.
        bool validatorCalled = false;
        string? Validator(string composed) { validatorCalled = true; return null; }

        Forms.AllowedDatatypePickerForm
            .ValidateComposition(Std("nvarchar"), "", Validator)
            .Should().Contain("requires a parameter");
        validatorCalled.Should().BeFalse();
    }
}
