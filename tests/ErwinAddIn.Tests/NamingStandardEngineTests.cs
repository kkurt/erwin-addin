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
        string errorMessage = "test error",
        RuleApplyOn applyOn = RuleApplyOn.Both)
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
            ApplyOn = applyOn,
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

    // ============================================================
    // Matrix coverage 2026-05-31. The full matrix is RuleType x
    // ApplyOn x IsRequired x AutoApply = 5 x 3 x 2 x 2 = 60. The
    // cases below lock the behaviour the live History/Log bug
    // depended on (rule#17 Vp prefix unconditional, rule#21 _HISTORY
    // suffix conditional Both, rule#1019 Required Name_Qualifier
    // Both, rule#1022 Length Definition Create) plus the
    // gap-analysis "must hold forever" invariants the production
    // pipeline relies on. New tests deliberately do NOT mock SCAPI -
    // every case is pure-engine (unconditional rules + name input
    // only) so they run in milliseconds and never depend on COM
    // marshalling.
    // ============================================================

    // ---------- MatchesApplyOn gate (3 x 2 truth table) ----------

    [Theory]
    [InlineData(RuleApplyOn.Create, true,  true)]
    [InlineData(RuleApplyOn.Create, false, false)]
    [InlineData(RuleApplyOn.Update, true,  false)]
    [InlineData(RuleApplyOn.Update, false, true)]
    [InlineData(RuleApplyOn.Both,   true,  true)]
    [InlineData(RuleApplyOn.Both,   false, true)]
    public void MatchesApplyOn_truth_table(RuleApplyOn applyOn, bool isNew, bool expected)
    {
        // Locks the 6-cell truth table that every rule kind shares.
        // The live History bug bypassed this gate by losing the
        // entity name across an auto-rename; if MatchesApplyOn itself
        // ever drifts, regressions surface here BEFORE shipping.
        var rule = Rule(NamingRuleKind.Prefix, prefix: "X", applyOn: applyOn);
        NamingValidationEngine.MatchesApplyOn(rule, isNew).Should().Be(expected);
    }

    // ---------- Length matrix (Create / Update / Both x Req T/F) ----------

    [Fact]
    public void Length_ApplyOn_Both_IsRequired_false_fires_on_empty()
    {
        // rule#1022 analogue with ApplyOn=Both: the History fix
        // exposed that some admins set Length rules to Both rather
        // than Create. Empty + not required + Length must still warn.
        var rule = Rule(NamingRuleKind.Length, lenOp: ">", lenVal: 10,
                        isRequired: false, applyOn: RuleApplyOn.Both);
        NamingValidationEngine.EvaluateRule(rule, "").Should().ContainSingle()
            .Which.RuleName.Should().Be("Length");
    }

    [Fact]
    public void Length_ApplyOn_Update_IsRequired_true_fires_required_on_empty()
    {
        // Update gate + IsRequired true + empty: Step 1 emits Required,
        // does NOT continue into Length pattern check (proven by
        // Empty_with_IsRequired_true_does_NOT_also_fire_pattern_check).
        // Locks the contract for the legacy-data-grandfathering use case
        // admin uses Update + IsRequired for.
        var rule = Rule(NamingRuleKind.Length, lenOp: ">", lenVal: 5,
                        isRequired: true, applyOn: RuleApplyOn.Update);
        var results = NamingValidationEngine.EvaluateRule(rule, "");
        results.Should().ContainSingle().Which.RuleName.Should().Be("Required");
    }

    // ---------- Required RuleType (first-class kind) ----------

    [Fact]
    public void Required_ruletype_fires_on_empty_even_when_IsRequired_flag_is_false()
    {
        // RuleType=Required implies "value must be non-empty"
        // regardless of the IsRequired flag (admin 2026-05-25 spec).
        // rule#1019 analogue.
        var rule = Rule(NamingRuleKind.Required, isRequired: false,
                        applyOn: RuleApplyOn.Both);
        NamingValidationEngine.EvaluateRule(rule, "").Should().ContainSingle()
            .Which.RuleName.Should().Be("Required");
    }

    [Fact]
    public void Required_ruletype_passes_when_value_is_non_empty()
    {
        var rule = Rule(NamingRuleKind.Required, isRequired: true,
                        applyOn: RuleApplyOn.Both);
        NamingValidationEngine.EvaluateRule(rule, "Customer").Should().BeEmpty();
    }

    // ---------- Prefix matrix ----------

    [Theory]
    [InlineData("DM_", "DM_CUSTOMER", true)]   // prefix present -> pass
    [InlineData("DM_", "CUSTOMER",    false)]  // prefix missing -> fire
    [InlineData("DM_", "dm_customer", true)]   // case-insensitive match
    public void Prefix_pattern_check_matrix(string prefix, string name, bool shouldPass)
    {
        var rule = Rule(NamingRuleKind.Prefix, prefix: prefix,
                        applyOn: RuleApplyOn.Both);
        var results = NamingValidationEngine.EvaluateRule(rule, name);
        if (shouldPass) results.Should().BeEmpty();
        else results.Should().ContainSingle().Which.RuleName.Should().Be("Prefix");
    }

    // ---------- Suffix matrix ----------

    [Theory]
    [InlineData("_T", "CUSTOMER_T", true)]
    [InlineData("_T", "CUSTOMER",   false)]
    [InlineData("_T", "customer_t", true)]
    public void Suffix_pattern_check_matrix(string suffix, string name, bool shouldPass)
    {
        var rule = Rule(NamingRuleKind.Suffix, suffix: suffix,
                        applyOn: RuleApplyOn.Both);
        var results = NamingValidationEngine.EvaluateRule(rule, name);
        if (shouldPass) results.Should().BeEmpty();
        else results.Should().ContainSingle().Which.RuleName.Should().Be("Suffix");
    }

    // ---------- ApplyNamingStandards: AutoApply x ApplyOn x isNew matrix --------

    [Fact]
    public void ApplyNamingStandards_unconditional_Prefix_Both_AutoApply_true_isNew_true_applies()
    {
        // rule#17 analogue: unconditional Vp prefix, ApplyOn=Both,
        // AutoApply=true. Must add Vp on the creation gesture.
        NamingStandardService.Instance.SeedForTesting(new[]
        {
            Rule(NamingRuleKind.Prefix, prefix: "Vp", autoApply: true,
                 applyOn: RuleApplyOn.Both),
        });
        try
        {
            var applied = NamingValidationEngine.ApplyNamingStandards(
                "Table", "owner_test", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: true);
            applied.Should().Be("Vpowner_test");
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    [Fact]
    public void ApplyNamingStandards_unconditional_Prefix_Both_AutoApply_true_isNew_false_still_applies()
    {
        // ApplyOn=Both must fire regardless of isNew. The History bug
        // depended on this guarantee even when the deferred check
        // path passed isNew=false.
        NamingStandardService.Instance.SeedForTesting(new[]
        {
            Rule(NamingRuleKind.Prefix, prefix: "Vp", autoApply: true,
                 applyOn: RuleApplyOn.Both),
        });
        try
        {
            var applied = NamingValidationEngine.ApplyNamingStandards(
                "Table", "owner_test", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: false);
            applied.Should().Be("Vpowner_test");
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    [Fact]
    public void ApplyNamingStandards_unconditional_Prefix_Create_AutoApply_true_isNew_false_does_NOT_apply()
    {
        // Spec: ApplyOn=Create filtered out when isNew=false. The
        // working Log case fires Vp on the SECOND scoped check
        // (isNew=true) because of this gate. Verifies the engine
        // honours ApplyOn=Create literally.
        NamingStandardService.Instance.SeedForTesting(new[]
        {
            Rule(NamingRuleKind.Prefix, prefix: "Vp", autoApply: true,
                 applyOn: RuleApplyOn.Create),
        });
        try
        {
            var applied = NamingValidationEngine.ApplyNamingStandards(
                "Table", "owner_test", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: false);
            applied.Should().Be("owner_test");
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    [Fact]
    public void ApplyNamingStandards_unconditional_Suffix_AutoApply_false_skipped_when_autoOnly_true()
    {
        // autoOnly:true is the silent auto-apply path. AutoApply=false
        // rules must be deferred to the manual ask-user path.
        NamingStandardService.Instance.SeedForTesting(new[]
        {
            Rule(NamingRuleKind.Suffix, suffix: "_T", autoApply: false,
                 applyOn: RuleApplyOn.Both),
        });
        try
        {
            NamingValidationEngine.ApplyNamingStandards(
                "Table", "Entity1", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: true).Should().Be("Entity1");

            NamingValidationEngine.ApplyNamingStandards(
                "Table", "Entity1", scapiObject: null, autoOnly: false,
                propertyCode: "Physical_Name", isNew: true).Should().Be("Entity1_T");
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    // ============================================================
    // 2026-05-31 corrected semantic (commits 2aca8cb + c50a5be
    // backed out): ApplyOn=Update rules MUST NEVER fire on a new
    // entity. The earlier "creationGesture widens the gate" detour
    // was the wrong direction - user explicit rule was "Parametre
    // (_PRM, Update) MUST NOT be added when the user creates a fresh
    // table". The strict 3x2 truth table is the only invariant; the
    // three locking tests below replace the deleted 12-row matrix.
    // ============================================================

    [Fact]
    public void ApplyOn_Update_rule_never_fires_on_placeholder_commit()
    {
        // THE live-bug regression seed (2026-05-31): rule#22
        // analogue. Update-only suffix on a new entity (isNew=true)
        // MUST stay silent so 'Tmp_Create_1' on TableClass=Parametre
        // becomes 'VpTmp_Create_1' (Vp from a Create / Both prefix
        // applied separately) - it must NEVER become
        // 'Tmp_Create_1_PRM'.
        NamingStandardService.Instance.SeedForTesting(new[]
        {
            Rule(NamingRuleKind.Suffix, suffix: "_PRM", autoApply: true,
                 applyOn: RuleApplyOn.Update),
        });
        try
        {
            // isNew=true (creation gesture) - Update rule must not fire.
            NamingValidationEngine.ApplyNamingStandards(
                "Table", "Tmp_Create_1", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: true).Should().Be("Tmp_Create_1");

            // isNew=false (real edit on an existing entity) - Update rule fires.
            NamingValidationEngine.ApplyNamingStandards(
                "Table", "Tmp_Create_1", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: false).Should().Be("Tmp_Create_1_PRM");
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    [Fact]
    public void ApplyOn_Create_rule_fires_only_on_isNew_true()
    {
        // rule#17 analogue (when admin marks Vp as ApplyOn=Create
        // rather than Both). Locks the other half of the strict
        // 3x2 truth table.
        NamingStandardService.Instance.SeedForTesting(new[]
        {
            Rule(NamingRuleKind.Prefix, prefix: "Vp", autoApply: true,
                 applyOn: RuleApplyOn.Create),
        });
        try
        {
            NamingValidationEngine.ApplyNamingStandards(
                "Table", "Foo", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: true).Should().Be("VpFoo");

            NamingValidationEngine.ApplyNamingStandards(
                "Table", "Foo", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: false).Should().Be("Foo");
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    [Fact]
    public void ApplyOn_Both_rule_fires_regardless_of_isNew()
    {
        // Third leg of the strict 3x2 truth table. Both never gets
        // filtered; this is the gate row that drives the working
        // Vp prefix application on every placeholder commit when
        // admin authored rule#17 as ApplyOn=Both.
        NamingStandardService.Instance.SeedForTesting(new[]
        {
            Rule(NamingRuleKind.Prefix, prefix: "Vp", autoApply: true,
                 applyOn: RuleApplyOn.Both),
        });
        try
        {
            NamingValidationEngine.ApplyNamingStandards(
                "Table", "Foo", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: true).Should().Be("VpFoo");

            NamingValidationEngine.ApplyNamingStandards(
                "Table", "Foo", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: false).Should().Be("VpFoo");
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    [Fact]
    public void ApplyNamingStandards_idempotent_does_not_double_apply_prefix()
    {
        // Already prefixed names are NOT re-prefixed (verified
        // case-insensitive via Prefix_pattern_check_matrix).
        NamingStandardService.Instance.SeedForTesting(new[]
        {
            Rule(NamingRuleKind.Prefix, prefix: "Vp", autoApply: true,
                 applyOn: RuleApplyOn.Both),
        });
        try
        {
            NamingValidationEngine.ApplyNamingStandards(
                "Table", "Vpowner_test", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: true).Should().Be("Vpowner_test");
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    // ---------- View.Name (non-Physical_Name property) auto-apply ----------

    [Fact]
    public void ApplyNamingStandards_Suffix_on_View_Name_property_auto_applies()
    {
        // Regression seed 2026-06-13: erwin r10 Views have NO Physical_Name
        // accessor, so a view's suffix/prefix rules are authored on "Name".
        // The orchestration (TableTypeMonitorService.AutoApplyNamingForProperty)
        // forwards the property code into the engine - this locks the engine
        // half of the contract: a Suffix rule keyed on "Name" must auto-apply
        // when ApplyNamingStandards is called with propertyCode="Name".
        // Before the fix the suffix was never added and the un-suffixed name
        // escalated to the Required-input popup (the VIEW.Name rule#1037).
        NamingStandardService.Instance.SeedForTesting(new[]
        {
            new NamingStandardRule
            {
                Id = 1037,
                ObjectType = "View",
                PropertyCode = "Name",
                RuleType = NamingRuleKind.Suffix,
                Suffix = "_VVV",
                AutoApply = true,
                ApplyOn = RuleApplyOn.Both,
                IsActive = true,
            },
        });
        try
        {
            // A Physical_Name run finds nothing (no rule on that code) and
            // leaves the name untouched - exactly why Step 1 was a no-op.
            NamingValidationEngine.ApplyNamingStandards(
                "View", "V_6", scapiObject: null, autoOnly: true,
                propertyCode: "Physical_Name", isNew: true).Should().Be("V_6");

            // The Name run (what the Step 3b loop now performs) applies _VVV.
            NamingValidationEngine.ApplyNamingStandards(
                "View", "V_6", scapiObject: null, autoOnly: true,
                propertyCode: "Name", isNew: true).Should().Be("V_6_VVV");

            // Idempotent: an already-suffixed name is not doubled.
            NamingValidationEngine.ApplyNamingStandards(
                "View", "V_6_VVV", scapiObject: null, autoOnly: true,
                propertyCode: "Name", isNew: true).Should().Be("V_6_VVV");
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    // ---- PK-membership condition (column-is-PK, resolved via Key_Group walk) ----
    // Pure: these exercise the static condition logic only (no singleton, no SCAPI).

    private static NamingStandardRule PkConditionRule(string propCode)
        => new()
        {
            Id = 1,
            ObjectType = "Column",
            PropertyCode = "Physical_Name",
            RuleType = NamingRuleKind.Template,
            ValueTemplate = "PK_{Table.Physical_Name}",
            TemplateFillMode = "Always",
            AutoApply = true,
            ApplyOn = RuleApplyOn.Both,
            IsActive = true,
            DependsOnPropertyDefId = 99,
            DependsOnPropertyCode = propCode,
            DependsOnPropertyValues = "True",
        };

    [Theory]
    [InlineData("IsPrimaryKey")]
    [InlineData("Is_PK")]
    [InlineData("Primary_Key")]
    [InlineData("primary_key")] // case-insensitive
    public void IsPkMembershipCondition_true_for_pk_property_codes(string code)
        => NamingValidationEngine.IsPkMembershipCondition(PkConditionRule(code)).Should().BeTrue();

    [Fact]
    public void IsPkMembershipCondition_false_for_ordinary_property()
        => NamingValidationEngine.IsPkMembershipCondition(PkConditionRule("Physical_Data_Type")).Should().BeFalse();

    [Fact]
    public void IsPkMembershipCondition_false_when_source_is_a_udp()
    {
        var rule = PkConditionRule("Is_PK");
        rule.DependsOnPropertyDefId = null;     // not a built-in property source
        rule.DependsOnUdpId = 5;                // a UDP source instead
        rule.DependsOnUdpName = "Is_PK";
        NamingValidationEngine.IsPkMembershipCondition(rule).Should().BeFalse();
    }

    [Fact]
    public void IsRuleApplicable_pk_condition_uses_caller_resolved_membership()
    {
        var rule = PkConditionRule("IsPrimaryKey"); // cond: IsPrimaryKey in [True]
        // No SCAPI object needed: the PK answer comes from the caller, not a read.
        NamingValidationEngine.IsRuleApplicable(rule, "Column", scapiObject: null, pkMembership: true).Should().BeTrue();
        NamingValidationEngine.IsRuleApplicable(rule, "Column", scapiObject: null, pkMembership: false).Should().BeFalse();
    }

    [Fact]
    public void IsRuleApplicable_pk_condition_without_membership_falls_back_and_is_not_applicable()
    {
        // null override + null object → the old property-read path → "" → not applicable.
        var rule = PkConditionRule("IsPrimaryKey");
        NamingValidationEngine.IsRuleApplicable(rule, "Column", scapiObject: null, pkMembership: null).Should().BeFalse();
    }

    [Fact]
    public void IsRuleApplicable_unconditional_rule_applies_regardless_of_membership()
    {
        var rule = new NamingStandardRule
        {
            Id = 2,
            ObjectType = "Column",
            PropertyCode = "Physical_Name",
            RuleType = NamingRuleKind.Template,
            IsActive = true,
            ApplyOn = RuleApplyOn.Both,
        };
        NamingValidationEngine.IsRuleApplicable(rule, "Column", scapiObject: null, pkMembership: null).Should().BeTrue();
    }
}
