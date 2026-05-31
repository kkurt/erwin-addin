using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Per-RuleType coverage for <see cref="NamingValidationEngine.EvaluateRule"/>.
/// The engine is pure (no SCAPI, no DB) once a single rule is in hand, so
/// each fixture builds a <see cref="NamingStandardRule"/> with exactly the
/// fields its kind needs and asserts the dispatch produces the right
/// violation - or none.
/// <para>
/// 2026-05-17 spec: four atomic <see cref="NamingRuleKind"/> values
/// (Prefix/Suffix/Length/Regexp) plus an orthogonal
/// <see cref="NamingStandardRule.IsRequired"/> flag that gates whether
/// empty values fire a violation. Empty + IS_REQUIRED=false short-circuits
/// without running the pattern check.
/// </para>
/// </summary>
public class NamingStandardEngineTests
{
    private static NamingStandardRule Rule(
        NamingRuleKind kind,
        string prefix = "",
        string suffix = "",
        string lenOp = "",
        int? lenVal = null,
        string regex = "",
        bool autoApply = false,
        bool isRequired = false,
        string errorMessage = "test error")
        => new()
        {
            Id = 1,
            ObjectType = "Table",
            PropertyCode = "Physical_Name",
            RuleType = kind,
            IsRequired = isRequired,
            Prefix = prefix,
            Suffix = suffix,
            LengthOperator = lenOp,
            LengthValue = lenVal,
            RegexpPattern = regex,
            ErrorMessage = errorMessage,
            AutoApply = autoApply,
            IsActive = true,
        };

    // ---------- IS_REQUIRED gate (Step 1) ----------

    [Fact]
    public void Empty_value_with_IsRequired_true_fires_required_violation()
    {
        var rule = Rule(NamingRuleKind.Prefix, prefix: "DM_", isRequired: true,
                        errorMessage: "Tablo adı boş bırakılamaz");
        var results = NamingValidationEngine.EvaluateRule(rule, "");
        results.Should().ContainSingle(r => !r.IsValid)
            .Which.Should().BeEquivalentTo(new
            {
                RuleName = "Required",
                ErrorMessage = "Tablo adı boş bırakılamaz",
            }, opt => opt.ExcludingMissingMembers());
    }

    [Fact]
    public void Whitespace_value_with_IsRequired_true_fires()
    {
        var rule = Rule(NamingRuleKind.Length, lenOp: ">=", lenVal: 5, isRequired: true);
        var results = NamingValidationEngine.EvaluateRule(rule, "   ");
        results.Should().ContainSingle(r => !r.IsValid)
            .Which.RuleName.Should().Be("Required");
    }

    [Fact]
    public void Empty_value_with_IsRequired_false_LENGTH_rule_still_fires()
    {
        // Spec (refined 2026-05-31): empty + not required short-circuits
        // for Prefix / Suffix / Regexp (those checks are meaningless on
        // empty), but Length WITH '>' or '>=' operator is meaningful even
        // on empty - "must be at least N characters" on an empty value
        // is a real, actionable violation (admin rule#1022:
        // TABLE.Definition len > 10 req=False, intended to warn when the
        // user leaves Comment blank).
        var rule = Rule(NamingRuleKind.Length, lenOp: ">=", lenVal: 5, isRequired: false);
        var results = NamingValidationEngine.EvaluateRule(rule, "");
        results.Should().ContainSingle(r => !r.IsValid)
            .Which.RuleName.Should().Be("Length");
    }

    [Fact]
    public void Empty_value_with_IsRequired_false_NON_LENGTH_rules_still_skip()
    {
        // Prefix / Suffix / Regexp do NOT fire on empty + not required -
        // those checks are meaningless on an empty value (a Prefix rule
        // saying "must start with 'Vp'" on an empty optional field
        // would emit a useless violation the user can never satisfy
        // without filling the field).
        var prefix = Rule(NamingRuleKind.Prefix, prefix: "DM_", isRequired: false);
        NamingValidationEngine.EvaluateRule(prefix, "").Should().BeEmpty();

        var suffix = Rule(NamingRuleKind.Suffix, suffix: "_T", isRequired: false);
        NamingValidationEngine.EvaluateRule(suffix, "").Should().BeEmpty();

        var regex = Rule(NamingRuleKind.Regexp, regex: "^[A-Z_]+$", isRequired: false);
        NamingValidationEngine.EvaluateRule(regex, "").Should().BeEmpty();
    }

    [Fact]
    public void Empty_with_IsRequired_true_does_NOT_also_fire_pattern_check()
    {
        // Step 1 returns after emitting the Required violation - the Prefix
        // pattern check (which would fail on '') must NOT also produce a
        // second violation.
        var rule = Rule(NamingRuleKind.Prefix, prefix: "DM_", isRequired: true);
        var results = NamingValidationEngine.EvaluateRule(rule, "");
        results.Should().HaveCount(1);
        results[0].RuleName.Should().Be("Required");
    }

    [Fact]
    public void NonEmpty_value_skips_required_branch_and_runs_pattern_check()
    {
        // IS_REQUIRED gate is only about emptiness - non-empty values
        // proceed to Step 3 regardless of the flag.
        var rule = Rule(NamingRuleKind.Prefix, prefix: "DM_", isRequired: true);
        var results = NamingValidationEngine.EvaluateRule(rule, "FOO");
        results.Should().ContainSingle()
            .Which.RuleName.Should().Be("Prefix");
    }

    // ---------- Prefix ----------

    [Fact]
    public void Prefix_missing_fires_with_custom_message()
    {
        var rule = Rule(NamingRuleKind.Prefix, prefix: "DM_", errorMessage: "Must start with DM_");
        var results = NamingValidationEngine.EvaluateRule(rule, "CUSTOMER");
        results.Should().ContainSingle(r => !r.IsValid)
            .Which.ErrorMessage.Should().Be("Must start with DM_");
    }

    [Fact]
    public void Prefix_present_passes_case_insensitive()
    {
        var rule = Rule(NamingRuleKind.Prefix, prefix: "DM_");
        var results = NamingValidationEngine.EvaluateRule(rule, "dm_customer");
        results.Should().BeEmpty();
    }

    [Fact]
    public void Prefix_empty_parameter_is_skipped_not_violated()
    {
        // Admin would normally reject this at save time, but defend against
        // hand-edited rows. No meaningless "must start with ''" violation.
        var rule = Rule(NamingRuleKind.Prefix, prefix: "");
        var results = NamingValidationEngine.EvaluateRule(rule, "FOO");
        results.Should().BeEmpty();
    }

    // ---------- Suffix ----------

    [Fact]
    public void Suffix_missing_fires()
    {
        var rule = Rule(NamingRuleKind.Suffix, suffix: "_T");
        var results = NamingValidationEngine.EvaluateRule(rule, "CUSTOMER");
        results.Should().ContainSingle(r => !r.IsValid)
            .Which.RuleName.Should().Be("Suffix");
    }

    [Fact]
    public void Suffix_present_passes()
    {
        var rule = Rule(NamingRuleKind.Suffix, suffix: "_T");
        var results = NamingValidationEngine.EvaluateRule(rule, "CUSTOMER_T");
        results.Should().BeEmpty();
    }

    // ---------- Length ----------

    [Theory]
    [InlineData(">=", 5, "AB", false)]       // length 2 < 5 -> violation
    [InlineData(">=", 5, "ABCDE", true)]     // length 5 >= 5 -> pass
    [InlineData("<=", 5, "ABCDEFG", false)]
    [InlineData("<=", 5, "ABCDE", true)]
    [InlineData(">", 0, "X", true)]
    [InlineData("<", 3, "ABC", false)]
    [InlineData("<", 3, "AB", true)]
    [InlineData("=", 3, "ABC", true)]
    [InlineData("=", 3, "ABCD", false)]
    public void Length_operator_evaluates_correctly(string op, int val, string name, bool shouldPass)
    {
        var rule = Rule(NamingRuleKind.Length, lenOp: op, lenVal: val);
        var results = NamingValidationEngine.EvaluateRule(rule, name);
        if (shouldPass)
            results.Should().BeEmpty();
        else
            results.Should().ContainSingle(r => !r.IsValid);
    }

    [Fact]
    public void Length_missing_value_is_skipped()
    {
        var rule = Rule(NamingRuleKind.Length, lenOp: ">=", lenVal: null);
        var results = NamingValidationEngine.EvaluateRule(rule, "ANY");
        results.Should().BeEmpty();
    }

    [Fact]
    public void Length_missing_operator_is_skipped()
    {
        var rule = Rule(NamingRuleKind.Length, lenOp: "", lenVal: 5);
        var results = NamingValidationEngine.EvaluateRule(rule, "ANY");
        results.Should().BeEmpty();
    }

    // ---------- Regexp ----------

    [Fact]
    public void Regexp_match_passes()
    {
        var rule = Rule(NamingRuleKind.Regexp, regex: "^[A-Z][A-Z0-9_]*$");
        var results = NamingValidationEngine.EvaluateRule(rule, "VALID_NAME_1");
        results.Should().BeEmpty();
    }

    [Fact]
    public void Regexp_mismatch_fires()
    {
        var rule = Rule(NamingRuleKind.Regexp, regex: "^[A-Z][A-Z0-9_]*$");
        var results = NamingValidationEngine.EvaluateRule(rule, "bad name with spaces");
        results.Should().ContainSingle(r => !r.IsValid)
            .Which.RuleName.Should().Be("Regexp");
    }

    [Fact]
    public void Regexp_empty_pattern_is_skipped()
    {
        var rule = Rule(NamingRuleKind.Regexp, regex: "");
        var results = NamingValidationEngine.EvaluateRule(rule, "ANY");
        results.Should().BeEmpty();
    }

    [Fact]
    public void Regexp_invalid_pattern_does_not_crash()
    {
        // Malformed pattern - regex compiler throws, engine swallows + logs,
        // emits no violation (admin gets to see the failure in debug log).
        var rule = Rule(NamingRuleKind.Regexp, regex: "[unclosed");
        var results = NamingValidationEngine.EvaluateRule(rule, "ANY");
        results.Should().BeEmpty();
    }

    // ---------- Cross-cutting ----------

    [Fact]
    public void Error_message_falls_back_to_default_when_empty_on_pattern()
    {
        var rule = Rule(NamingRuleKind.Prefix, prefix: "ABC_", errorMessage: "");
        var results = NamingValidationEngine.EvaluateRule(rule, "FOO");
        results.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("Name must start with 'ABC_'");
    }

    [Fact]
    public void Error_message_falls_back_to_default_on_required_violation()
    {
        var rule = Rule(NamingRuleKind.Prefix, prefix: "ABC_", isRequired: true, errorMessage: "");
        var results = NamingValidationEngine.EvaluateRule(rule, "");
        results.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("Value is required");
    }

    [Fact]
    public void Null_rule_returns_empty()
    {
        var results = NamingValidationEngine.EvaluateRule(null!, "ANY");
        results.Should().BeEmpty();
    }

    [Fact]
    public void Null_object_name_is_treated_as_empty_for_required_gate()
    {
        var rule = Rule(NamingRuleKind.Prefix, prefix: "X", isRequired: true);
        var results = NamingValidationEngine.EvaluateRule(rule, null!);
        results.Should().ContainSingle(r => !r.IsValid)
            .Which.RuleName.Should().Be("Required");
    }

    [Fact]
    public void Null_object_name_with_IsRequired_false_LENGTH_rule_fires()
    {
        // Refined 2026-05-31: Length rules are evaluated even on null/
        // empty values when IsRequired is false (admin's "> N" is a
        // meaningful "at-least N characters" expectation regardless of
        // whether the user has typed anything yet). Non-Length rules
        // still short-circuit in that case (covered by the Prefix /
        // Suffix / Regexp variant above).
        var rule = Rule(NamingRuleKind.Length, lenOp: ">", lenVal: 0, isRequired: false);
        var results = NamingValidationEngine.EvaluateRule(rule, null!);
        results.Should().ContainSingle(r => !r.IsValid)
            .Which.RuleName.Should().Be("Length");
    }

    // ---------- C3 condition: CSV IN-match ----------

    [Theory]
    [InlineData("DateTime", "DateTime,Date,Timestamp", true)]        // exact, position 1
    [InlineData("Date",     "DateTime,Date,Timestamp", true)]        // exact, position 2
    [InlineData("datetime", "DateTime,Date,Timestamp", true)]        // case-insensitive
    [InlineData("Char",     "DateTime,Date,Timestamp", false)]       // not in list
    [InlineData("Date",     "Date",                    true)]        // single-value CSV (back-compat)
    [InlineData("Date",     "Datetime2",               false)]       // substring is NOT a match
    [InlineData("  Date  ", "Date,DateTime",           false)]       // source value not trimmed by us
    [InlineData("Date",     " Date , DateTime ",       true)]        // CSV tokens trimmed
    public void MatchesCsv_handles_value_vs_list(string sourceValue, string csv, bool expected)
    {
        NamingValidationEngine.MatchesCsv(sourceValue, csv).Should().Be(expected);
    }

    [Fact]
    public void MatchesCsv_empty_csv_with_value_matches_any_nonempty()
    {
        // Spec: empty CSV with a source set means "any non-empty value matches"
        // (the rule fires as soon as the source property has a value).
        NamingValidationEngine.MatchesCsv("anything", "").Should().BeTrue();
        NamingValidationEngine.MatchesCsv("anything", null!).Should().BeTrue();
    }

    [Fact]
    public void MatchesCsv_empty_csv_and_empty_value_does_not_match()
    {
        // Spec implication: empty value means "no signal" - the source
        // property is unset, so even the permissive "any non-empty" path
        // does not fire.
        NamingValidationEngine.MatchesCsv("", "").Should().BeFalse();
        NamingValidationEngine.MatchesCsv(null!, null!).Should().BeFalse();
    }

    [Fact]
    public void MatchesCsv_skips_empty_tokens()
    {
        // ", ,Date" should still match "Date" - empty tokens between
        // commas are ignored rather than treated as "match empty source".
        NamingValidationEngine.MatchesCsv("Date", ", ,Date").Should().BeTrue();
        NamingValidationEngine.MatchesCsv("",     ", ,Date").Should().BeFalse();
    }
}
