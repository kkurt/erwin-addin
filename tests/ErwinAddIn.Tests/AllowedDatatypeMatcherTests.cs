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

    // ---------- ANY-match: same name defined several times (unique index dropped 2026-07-14) ----------
    // A used datatype is valid when it satisfies the rule of ANY same-named entry; invalid only when
    // the base name matches one or more entries but the value satisfies NONE. Order-independent.

    [Fact]
    public void AnyMatch_value_valid_when_a_sibling_regex_accepts_it()
    {
        // Two "nvarchar" REGEX rows: ^\d{1,4}$ (max 4 digits) and ^\d{1,10}$ (max 10). A 5-digit
        // length is REJECTED by the first and ACCEPTED by the second, so ANY-match makes it valid.
        // (The task's "8000" is only 4 digits and would already pass the first row; 50000 forces the
        //  scan to fall through a real failure onto the accepting sibling.)
        var forward = Set(Rgx("nvarchar", @"^\d{1,4}$"), Rgx("nvarchar", @"^\d{1,10}$"));
        AllowedDatatypeService.ValidateDatatype("nvarchar(50000)", forward).IsValid.Should().BeTrue();

        // Same assertion with the rows REVERSED proves the verdict does not depend on entry order.
        var reversed = Set(Rgx("nvarchar", @"^\d{1,10}$"), Rgx("nvarchar", @"^\d{1,4}$"));
        AllowedDatatypeService.ValidateDatatype("nvarchar(50000)", reversed).IsValid.Should().BeTrue();
    }

    [Fact]
    public void AnyMatch_invalid_when_no_sibling_accepts_and_message_names_the_base()
    {
        // Two "nvarchar" rows, neither accepts a 5-digit length -> Invalid, with a message that still
        // explains the failure (names the base). Order-independent: both arrangements reject.
        var forward = Set(Rgx("nvarchar", @"^\d{1,3}$"), Rgx("nvarchar", @"^\d{1,4}$"));
        var r = AllowedDatatypeService.ValidateDatatype("nvarchar(50000)", forward);
        r.IsValid.Should().BeFalse();
        r.Message.Should().Contain("nvarchar");

        AllowedDatatypeService.ValidateDatatype(
            "nvarchar(50000)", Set(Rgx("nvarchar", @"^\d{1,4}$"), Rgx("nvarchar", @"^\d{1,3}$")))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void AnyMatch_single_matching_row_that_rejects_keeps_its_verbatim_message()
    {
        // Exactly one matching entry that fails -> its OWN message is surfaced byte-for-byte (no
        // aggregate wrapper), so single-row behavior is unchanged by the ANY-match aggregation.
        var allowed = Set(Rgx("varchar2", @"^\d{1,3}$", error: "varchar2 length must be 1-999."));
        var r = AllowedDatatypeService.ValidateDatatype("varchar2(5000)", allowed);
        r.IsValid.Should().BeFalse();
        r.Message.Should().Be("varchar2 length must be 1-999.");
    }

    [Fact]
    public void AnyMatch_across_none_and_standard_siblings()
    {
        // Same name defined twice: one NONE (bare-only) + one STANDARD (parameter required). ANY-match:
        // the bare form is valid via the NONE row, the parameterized form via the STANDARD row - the
        // scan falls through the row that rejects onto the one that accepts, in either order.
        var allowed = Set(None("nvarchar"), Std("nvarchar"));
        AllowedDatatypeService.ValidateDatatype("nvarchar", allowed).IsValid.Should().BeTrue();      // NONE row
        AllowedDatatypeService.ValidateDatatype("nvarchar(30)", allowed).IsValid.Should().BeTrue();  // STANDARD row

        var reversed = Set(Std("nvarchar"), None("nvarchar"));
        AllowedDatatypeService.ValidateDatatype("nvarchar", reversed).IsValid.Should().BeTrue();
        AllowedDatatypeService.ValidateDatatype("nvarchar(30)", reversed).IsValid.Should().BeTrue();
    }

    // ---------- STRUCTURED (WP 323): p[,s][ suffix] with per-entry bounds and part modes ----------

    private static AllowedDatatypeEntry Structured(
        string name,
        int? pMin = null, int? pMax = null,
        StructuredPartMode scaleMode = StructuredPartMode.None, int? sMin = null, int? sMax = null,
        StructuredPartMode suffixMode = StructuredPartMode.None, string? suffixValues = null,
        bool allowBare = false) =>
        new()
        {
            Datatype = name,
            ParametrizationType = DatatypeParametrization.Structured,
            AllowNonParametrized = allowBare,
            ParamMin = pMin,
            ParamMax = pMax,
            ScaleMode = scaleMode,
            ScaleMin = sMin,
            ScaleMax = sMax,
            SuffixMode = suffixMode,
            SuffixValues = suffixValues,
        };

    [Theory]
    [InlineData("NUMBER(1)", true)]    // lower bound inclusive
    [InlineData("NUMBER(38)", true)]   // upper bound inclusive
    [InlineData("NUMBER(0)", false)]   // below PARAM_MIN
    [InlineData("NUMBER(39)", false)]  // above PARAM_MAX
    public void Structured_p_bounds_inclusive(string value, bool expected)
    {
        var allowed = Set(Structured("NUMBER", pMin: 1, pMax: 38));
        AllowedDatatypeService.ValidateDatatype(value, allowed).IsValid.Should().Be(expected);
    }

    [Fact]
    public void Structured_p_bound_messages_cover_two_and_one_sided_bounds()
    {
        AllowedDatatypeService.ValidateDatatype("NUMBER(39)", Set(Structured("NUMBER", pMin: 1, pMax: 38)))
            .Message.Should().Be("Length/precision must be between 1 and 38.");
        AllowedDatatypeService.ValidateDatatype("NUMBER(4)", Set(Structured("NUMBER", pMin: 5)))
            .Message.Should().Be("Length/precision must be at least 5.");
        AllowedDatatypeService.ValidateDatatype("NUMBER(11)", Set(Structured("NUMBER", pMax: 10)))
            .Message.Should().Be("Length/precision must be at most 10.");
    }

    [Fact]
    public void Structured_null_bounds_are_unbounded()
    {
        // PARAM_MIN/MAX both NULL = any parseable p, including 0 and very large values
        // (the admin guarantees >= 1 only when a lower bound IS set).
        var allowed = Set(Structured("NUMBER"));
        AllowedDatatypeService.ValidateDatatype("NUMBER(0)", allowed).IsValid.Should().BeTrue();
        AllowedDatatypeService.ValidateDatatype("NUMBER(999999999)", allowed).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Structured_scale_none_rejects_a_scale()
    {
        var r = AllowedDatatypeService.ValidateDatatype("NUMBER(10,2)", Set(Structured("NUMBER")));
        r.IsValid.Should().BeFalse();
        r.Message.Should().Be("Type 'NUMBER' does not take a scale.");
    }

    [Fact]
    public void Structured_scale_required_and_optional_presence_rules()
    {
        var required = Set(Structured("NUMBER", scaleMode: StructuredPartMode.Required));
        AllowedDatatypeService.ValidateDatatype("NUMBER(10,2)", required).IsValid.Should().BeTrue();
        var missing = AllowedDatatypeService.ValidateDatatype("NUMBER(10)", required);
        missing.IsValid.Should().BeFalse();
        missing.Message.Should().Be("Type 'NUMBER' requires a scale.");

        var optional = Set(Structured("NUMBER", scaleMode: StructuredPartMode.Optional));
        AllowedDatatypeService.ValidateDatatype("NUMBER(10)", optional).IsValid.Should().BeTrue();
        AllowedDatatypeService.ValidateDatatype("NUMBER(10,2)", optional).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("NUMBER(10,-84)", true)]   // negative lower bound inclusive (Oracle)
    [InlineData("NUMBER(10,127)", true)]
    [InlineData("NUMBER(10,-85)", false)]
    [InlineData("NUMBER(10,128)", false)]
    public void Structured_scale_bounds_allow_negative_scale(string value, bool expected)
    {
        var allowed = Set(Structured("NUMBER",
            scaleMode: StructuredPartMode.Optional, sMin: -84, sMax: 127));
        var r = AllowedDatatypeService.ValidateDatatype(value, allowed);
        r.IsValid.Should().Be(expected);
        if (!expected) r.Message.Should().Be("Scale must be between -84 and 127.");
    }

    [Fact]
    public void Structured_suffix_none_rejects_a_suffix()
    {
        var r = AllowedDatatypeService.ValidateDatatype("VARCHAR2(10 CHAR)", Set(Structured("VARCHAR2")));
        r.IsValid.Should().BeFalse();
        r.Message.Should().Be("Type 'VARCHAR2' does not take a length semantics suffix.");
    }

    [Fact]
    public void Structured_suffix_required_needs_a_listed_value()
    {
        var allowed = Set(Structured("VARCHAR2",
            suffixMode: StructuredPartMode.Required, suffixValues: "BYTE,CHAR"));
        AllowedDatatypeService.ValidateDatatype("VARCHAR2(10 CHAR)", allowed).IsValid.Should().BeTrue();
        var missing = AllowedDatatypeService.ValidateDatatype("VARCHAR2(10)", allowed);
        missing.IsValid.Should().BeFalse();
        missing.Message.Should().Be("Type 'VARCHAR2' requires a length semantics suffix (BYTE, CHAR).");
    }

    [Theory]
    [InlineData("VARCHAR2(10 CHAR)", true)]
    [InlineData("VARCHAR2(10 char)", true)]  // OrdinalIgnoreCase list match
    [InlineData("VARCHAR2(10 Byte)", true)]
    [InlineData("VARCHAR2(10 FOO)", false)]  // not in SUFFIX_VALUES
    [InlineData("VARCHAR2(10)", true)]       // OPTIONAL: suffix may be absent
    public void Structured_suffix_optional_matches_case_insensitively(string value, bool expected)
    {
        var allowed = Set(Structured("VARCHAR2",
            suffixMode: StructuredPartMode.Optional, suffixValues: "BYTE,CHAR"));
        var r = AllowedDatatypeService.ValidateDatatype(value, allowed);
        r.IsValid.Should().Be(expected);
        if (!expected) r.Message.Should().Be("Length semantics must be one of: BYTE, CHAR.");
    }

    [Fact]
    public void Structured_suffix_token_with_inner_space_matches()
    {
        // SUFFIX_VALUES tokens may contain inner whitespace ("XYZ 2"); the parser hands the
        // whole post-number tail over and the list match must land on it.
        var allowed = Set(Structured("NUMBER",
            scaleMode: StructuredPartMode.Optional,
            suffixMode: StructuredPartMode.Optional, suffixValues: "BYTE,XYZ 2"));
        AllowedDatatypeService.ValidateDatatype("NUMBER(22,5 XYZ 2)", allowed).IsValid.Should().BeTrue();
        AllowedDatatypeService.ValidateDatatype("NUMBER(22,5 XYZ 3)", allowed).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Structured_suffix_required_with_empty_values_is_unenforceable_and_accepts()
    {
        // Inconsistent admin data (REQUIRED but no allowed values): the rule is unenforceable -
        // nothing could ever satisfy it and the picker cannot even offer a choice - so BOTH the
        // absent- and present-suffix paths accept (with a debug log), mirroring the broken-regex
        // neutralization precedent. This also keeps the fallback synthesis round-trip true.
        var allowed = Set(Structured("NUMBER", pMin: 1,
            suffixMode: StructuredPartMode.Required, suffixValues: null));
        AllowedDatatypeService.ValidateDatatype("NUMBER(10)", allowed).IsValid.Should().BeTrue();
        AllowedDatatypeService.ValidateDatatype("NUMBER(10 ANY)", allowed).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Structured_bare_usage_follows_allow_non_parametrized()
    {
        var strict = AllowedDatatypeService.ValidateDatatype("NUMBER", Set(Structured("NUMBER", pMin: 1)));
        strict.IsValid.Should().BeFalse();
        strict.Message.Should().Be("Type 'NUMBER' requires a parameter."); // same wording as STANDARD/REGEX

        AllowedDatatypeService.ValidateDatatype("NUMBER", Set(Structured("NUMBER", pMin: 1, allowBare: true)))
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("VARCHAR2(5CHAR)")]  // digits glued to the suffix (no space)
    [InlineData("VARCHAR2()")]       // empty paren content
    [InlineData("VARCHAR2(abc)")]
    [InlineData("VARCHAR2(10,)")]
    public void Structured_unparseable_content_is_invalid_with_a_generated_message(string value)
    {
        var allowed = Set(Structured("VARCHAR2",
            suffixMode: StructuredPartMode.Optional, suffixValues: "BYTE,CHAR"));
        var r = AllowedDatatypeService.ValidateDatatype(value, allowed);
        r.IsValid.Should().BeFalse();
        r.Message.Should().Contain("is not valid for type 'VARCHAR2'");
    }

    [Fact]
    public void Structured_raw_erwin_whitespace_around_comma_is_accepted()
    {
        // Raw erwin values arrive unnormalized: "10 , 2" must classify like "10,2".
        var allowed = Set(Structured("NUMBER", pMin: 1, pMax: 38, scaleMode: StructuredPartMode.Optional));
        AllowedDatatypeService.ValidateDatatype("NUMBER(10 , 2)", allowed).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Structured_describe_rules_summary()
    {
        var entry = Structured("NUMBER", pMin: 1, pMax: 38,
            scaleMode: StructuredPartMode.Required, sMin: -84, sMax: 127,
            suffixMode: StructuredPartMode.Optional, suffixValues: "BYTE, CHAR");
        AllowedDatatypeService.DescribeStructuredRules(entry)
            .Should().Be("p 1..38, s! -84..127, sfx? BYTE|CHAR");
        AllowedDatatypeService.DescribeStructuredRules(Structured("X"))
            .Should().Be("p *..*");
        AllowedDatatypeService.DescribeStructuredRules(Std("nvarchar")).Should().BeEmpty();
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
    public void GetFallbackDatatype_structured_synthesizes_required_scale_and_round_trips()
    {
        // p = PARAM_MIN, ",SCALE_MIN" because the scale is REQUIRED; the token must satisfy the
        // very entry it was synthesized from (no revert loop).
        AllowedDatatypeService.Instance.SeedForTesting(new[]
        {
            Structured("NUMBER", pMin: 5, pMax: 38,
                scaleMode: StructuredPartMode.Required, sMin: 2, sMax: 5),
        });
        try
        {
            var fb = AllowedDatatypeService.Instance.GetFallbackDatatype();
            fb.Should().Be("NUMBER(5,2)");
            AllowedDatatypeService.Instance.IsAllowed(fb!).Should().BeTrue();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_structured_synthesizes_required_suffix_and_defaults()
    {
        // PARAM_MIN null -> p=1; SCALE_MIN null with scale REQUIRED -> 0; suffix REQUIRED -> the
        // FIRST allowed suffix.
        AllowedDatatypeService.Instance.SeedForTesting(new[]
        {
            Structured("VARCHAR2",
                scaleMode: StructuredPartMode.Required,
                suffixMode: StructuredPartMode.Required, suffixValues: "BYTE,CHAR"),
        });
        try
        {
            var fb = AllowedDatatypeService.Instance.GetFallbackDatatype();
            fb.Should().Be("VARCHAR2(1,0 BYTE)");
            AllowedDatatypeService.Instance.IsAllowed(fb!).Should().BeTrue();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_structured_scale_seed_clamps_to_a_negative_scale_max()
    {
        // SCALE_MIN null + SCALE_MAX -1: the default seed 0 would violate "at most -1", so the
        // synthesis clamps to the opposite bound and the fallback still round-trips.
        AllowedDatatypeService.Instance.SeedForTesting(new[]
        {
            Structured("NUMBER", scaleMode: StructuredPartMode.Required, sMax: -1),
        });
        try
        {
            var fb = AllowedDatatypeService.Instance.GetFallbackDatatype();
            fb.Should().Be("NUMBER(1,-1)");
            AllowedDatatypeService.Instance.IsAllowed(fb!).Should().BeTrue();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_structured_required_suffix_with_empty_values_round_trips()
    {
        // REQUIRED suffix but empty SUFFIX_VALUES (inconsistent admin data): the synthesis can
        // append nothing, and the validator's unenforceable-rule defense must accept the result.
        AllowedDatatypeService.Instance.SeedForTesting(new[]
        {
            Structured("NUMBER", pMin: 2,
                suffixMode: StructuredPartMode.Required, suffixValues: "  "),
        });
        try
        {
            var fb = AllowedDatatypeService.Instance.GetFallbackDatatype();
            fb.Should().Be("NUMBER(2)");
            AllowedDatatypeService.Instance.IsAllowed(fb!).Should().BeTrue();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>()); }
    }

    [Fact]
    public void GetFallbackDatatype_structured_omits_optional_parts()
    {
        AllowedDatatypeService.Instance.SeedForTesting(new[]
        {
            Structured("NUMBER", pMin: 3,
                scaleMode: StructuredPartMode.Optional,
                suffixMode: StructuredPartMode.Optional, suffixValues: "BYTE,CHAR"),
        });
        try
        {
            var fb = AllowedDatatypeService.Instance.GetFallbackDatatype();
            fb.Should().Be("NUMBER(3)");
            AllowedDatatypeService.Instance.IsAllowed(fb!).Should().BeTrue();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>()); }
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

    [Fact]
    public void SeedForTesting_sets_the_library_enabled_and_always_ask_flags()
    {
        // The two config flags (DATATYPE_LIBRARY_ENABLED / DATATYPE_LIBRARY_ALWAYS_ASK) gate the
        // enforce path in ValidationCoordinatorService; lock the property + seed contract here.
        AllowedDatatypeService.Instance.SeedForTesting(new[] { None("int") }, libraryEnabled: false, alwaysAsk: true);
        try
        {
            AllowedDatatypeService.Instance.LibraryEnabled.Should().BeFalse();
            AllowedDatatypeService.Instance.AlwaysAsk.Should().BeTrue();
            // The pure matcher is independent of the flags - a seeded whitelist still classifies.
            AllowedDatatypeService.Instance.HasRestriction.Should().BeTrue();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>()); }

        // Default seed -> enabled true, alwaysAsk false.
        AllowedDatatypeService.Instance.SeedForTesting(new[] { None("int") });
        try
        {
            AllowedDatatypeService.Instance.LibraryEnabled.Should().BeTrue();
            AllowedDatatypeService.Instance.AlwaysAsk.Should().BeFalse();
        }
        finally { AllowedDatatypeService.Instance.SeedForTesting(Array.Empty<AllowedDatatypeEntry>()); }
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

    // ---------- FormatComboLabel: admin LABEL in the dropdown, base-token fallback (WP 303) ----------

    [Fact]
    public void FormatComboLabel_uses_the_admin_label_when_set()
    {
        var entry = new AllowedDatatypeEntry
        {
            Datatype = "nvarchar",
            ParametrizationType = DatatypeParametrization.Regex,
            Label = "nvarchar(max)",
        };
        Forms.AllowedDatatypePickerForm.FormatComboLabel(entry).Should().Be("nvarchar(max)");
    }

    [Fact]
    public void FormatComboLabel_trims_the_label()
    {
        var entry = new AllowedDatatypeEntry { Datatype = "int", Label = "  Whole number  " };
        Forms.AllowedDatatypePickerForm.FormatComboLabel(entry).Should().Be("Whole number");
    }

    [Fact]
    public void FormatComboLabel_falls_back_to_base_token_with_n_for_parametric()
    {
        var entry = new AllowedDatatypeEntry
        {
            Datatype = "nvarchar",
            ParametrizationType = DatatypeParametrization.Standard, // takes a parameter
            Label = null,
        };
        Forms.AllowedDatatypePickerForm.FormatComboLabel(entry).Should().Be("nvarchar (n)");
    }

    [Fact]
    public void FormatComboLabel_falls_back_to_bare_base_token_for_none()
    {
        var entry = new AllowedDatatypeEntry
        {
            Datatype = "int",
            ParametrizationType = DatatypeParametrization.None,
            Label = "   ", // blank -> fallback
        };
        Forms.AllowedDatatypePickerForm.FormatComboLabel(entry).Should().Be("int");
    }

    [Fact]
    public void FormatComboLabel_distinguishes_two_same_named_rows_by_label()
    {
        // The WP 303 scenario: two "nvarchar" rows, same base token, different rules -> distinct
        // labels so the picker dropdown shows them as separate, choosable entries.
        var wide = new AllowedDatatypeEntry { Datatype = "nvarchar", ParametrizationType = DatatypeParametrization.Regex, Label = "nvarchar(max)" };
        var bounded = new AllowedDatatypeEntry { Datatype = "nvarchar", ParametrizationType = DatatypeParametrization.Regex, Label = "nvarchar(<=4000)" };
        Forms.AllowedDatatypePickerForm.FormatComboLabel(wide).Should().Be("nvarchar(max)");
        Forms.AllowedDatatypePickerForm.FormatComboLabel(bounded).Should().Be("nvarchar(<=4000)");
        Forms.AllowedDatatypePickerForm.FormatComboLabel(wide)
            .Should().NotBe(Forms.AllowedDatatypePickerForm.FormatComboLabel(bounded));
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

    // ---------- STRUCTURED entries through the picker's accept decision (WP 323) ----------

    private static AllowedDatatypeEntry Structured(
        string name,
        int? pMin = null, int? pMax = null,
        StructuredPartMode scaleMode = StructuredPartMode.None, int? sMin = null, int? sMax = null,
        StructuredPartMode suffixMode = StructuredPartMode.None, string? suffixValues = null,
        bool allowBare = false) =>
        new()
        {
            Datatype = name,
            ParametrizationType = DatatypeParametrization.Structured,
            AllowNonParametrized = allowBare,
            ParamMin = pMin,
            ParamMax = pMax,
            ScaleMode = scaleMode,
            ScaleMin = sMin,
            ScaleMax = sMax,
            SuffixMode = suffixMode,
            SuffixValues = suffixValues,
        };

    [Fact]
    public void ValidateComposition_structured_accepts_a_valid_structured_param()
    {
        var entry = Structured("VARCHAR2", pMin: 1, pMax: 4000,
            suffixMode: StructuredPartMode.Optional, suffixValues: "BYTE,CHAR");
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "15 CHAR", null).Should().BeNull();
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "15", null).Should().BeNull();
    }

    [Fact]
    public void ValidateComposition_structured_surfaces_the_structured_rule_messages()
    {
        var entry = Structured("VARCHAR2", pMin: 1, pMax: 4000,
            suffixMode: StructuredPartMode.Optional, suffixValues: "BYTE,CHAR");
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "15 FOO", null)
            .Should().Be("Length semantics must be one of: BYTE, CHAR.");
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "4001", null)
            .Should().Be("Length/precision must be between 1 and 4000.");
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "5CHAR", null)
            .Should().Contain("is not valid for type 'VARCHAR2'");
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "", null)
            .Should().Contain("requires a parameter");
    }

    [Fact]
    public void ValidateComposition_structured_bare_allowed_accepts_empty_param()
    {
        var entry = Structured("NUMBER", pMin: 1, allowBare: true);
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "", null).Should().BeNull();
    }

    [Fact]
    public void ValidateComposition_structured_reports_the_failed_part_for_focus_routing()
    {
        var entry = Structured("NUMBER", pMin: 1, pMax: 38,
            scaleMode: StructuredPartMode.Required, sMin: 0, sMax: 5,
            suffixMode: StructuredPartMode.Optional, suffixValues: "BYTE,CHAR");

        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "39,2", null, out var pPart);
        pPart.Should().Be(StructuredParamPart.Length);
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "10", null, out var missingScale);
        missingScale.Should().Be(StructuredParamPart.Scale);
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "10,9", null, out var scaleBound);
        scaleBound.Should().Be(StructuredParamPart.Scale);
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "10,2 FOO", null, out var suffixPart);
        suffixPart.Should().Be(StructuredParamPart.Suffix);
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "abc", null, out var parsePart);
        parsePart.Should().Be(StructuredParamPart.Length);
        Forms.AllowedDatatypePickerForm.ValidateComposition(entry, "10,2 BYTE", null, out var okPart)
            .Should().BeNull();
        okPart.Should().Be(StructuredParamPart.None);
    }

    [Fact]
    public void Compose_wraps_a_structured_param_verbatim()
    {
        // The structured surface recombines p[,s][ suffix] and hands it to the UNCHANGED Compose:
        // comma normalization is a no-op on the pre-normalized text and the suffix space survives.
        Forms.AllowedDatatypePickerForm.Compose("NUMBER", "22,5 XYZ").Should().Be("NUMBER(22,5 XYZ)");
        Forms.AllowedDatatypePickerForm.Compose("VARCHAR2", "15 CHAR").Should().Be("VARCHAR2(15 CHAR)");
    }

    [Fact]
    public void FormatComboLabel_structured_without_label_falls_back_to_parametric_token()
    {
        Forms.AllowedDatatypePickerForm.FormatComboLabel(Structured("NUMBER", pMin: 1))
            .Should().Be("NUMBER (n)");
    }
}

/// <summary>
/// Grammar coverage for <see cref="StructuredParamParser"/>: the paren-INSIDE content of a
/// STRUCTURED datatype (<c>p[,s][ suffix]</c>). NOT the paren-outside tail that
/// DataTypeParser.Parts.Suffix captures ("TIMESTAMP(6) WITH TIME ZONE").
/// </summary>
public class StructuredParamParserTests
{
    [Theory]
    [InlineData("22,5", 22, 5, "")]
    [InlineData("15 CHAR", 15, null, "CHAR")]
    [InlineData("22,5 XYZ", 22, 5, "XYZ")]
    [InlineData("22,5 XYZ 2", 22, 5, "XYZ 2")]     // suffix keeps inner spaces + digit fragments
    [InlineData("10 , 2", 10, 2, "")]              // raw erwin whitespace around the comma
    [InlineData("10, 2", 10, 2, "")]
    [InlineData("10 ,2", 10, 2, "")]
    [InlineData("22,-5", 22, -5, "")]              // signed scale (Oracle -84)
    [InlineData("22,+5", 22, 5, "")]
    [InlineData(" 18 ", 18, null, "")]             // outer whitespace ignored
    [InlineData("18   BYTE  ", 18, null, "BYTE")]  // multiple spaces before, trailing after
    [InlineData("0", 0, null, "")]                 // grammar allows 0; PARAM_MIN owns the bound
    public void TryParse_accepts_the_grammar(string content, int expectedP, int? expectedS, string expectedSuffix)
    {
        StructuredParamParser.TryParse(content, out int p, out int? s, out string suffix).Should().BeTrue();
        p.Should().Be(expectedP);
        s.Should().Be(expectedS);
        suffix.Should().Be(expectedSuffix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("5CHAR")]           // digits glued to the suffix - a space is mandatory
    [InlineData("abc")]
    [InlineData("CHAR")]            // no leading number
    [InlineData("10,")]             // dangling comma
    [InlineData(",5")]              // missing p
    [InlineData("-5")]              // p must be unsigned
    [InlineData("10,2,3")]          // second comma is not part of the grammar
    [InlineData("99999999999")]     // p overflows Int32
    [InlineData("10,99999999999")]  // s overflows Int32
    public void TryParse_rejects_non_grammar_content(string? content)
    {
        StructuredParamParser.TryParse(content!, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_outputs_are_reset_on_failure()
    {
        StructuredParamParser.TryParse("nope", out int p, out int? s, out string suffix).Should().BeFalse();
        p.Should().Be(0);
        s.Should().BeNull();
        suffix.Should().BeEmpty();
    }
}
