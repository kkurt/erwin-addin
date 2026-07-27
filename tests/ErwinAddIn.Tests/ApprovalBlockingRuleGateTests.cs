using System;
using System.Collections.Generic;
using System.Linq;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Coverage for the approval-submission blocking gate (admin
/// ENFORCE_APPROVAL_BLOCKING_RULES + per-rule MC_NAMING_STANDARD.BLOCKS_DDL_APPROVEMENT,
/// 2026-07-25). The gate refuses Integrate / Promote / Send-to-Approve submissions for a
/// violating model; DDL generation itself stays ungated.
///
/// Three seams are testable without a database or erwin:
/// (1) which rules <see cref="NamingStandardService.GetApprovalBlockingRules"/> surfaces,
/// (2) the structural preflight <see cref="ApprovalBlockingRuleGate.GetUnevaluatableReason"/>
///     that turns every "this rule can never produce a verdict" case into a hard failure
///     instead of the silent pass the evaluation engine would otherwise give, and
/// (3) <see cref="ApprovalBlockingRuleGate.EvaluateRuleAgainstObject"/>, the atomic
///     rule-vs-object decision, exercised against hand-rolled fake SCAPI objects.
/// The SCAPI model walk itself (Collect over Entity/Attribute/View/...) needs a live
/// erwin and is out of unit-test reach.
/// </summary>
// Seeds the NamingStandardService.Instance SINGLETON, so it must share the collection
// with every other class that does - otherwise xUnit's cross-class parallelism races the
// seed against their reads (passes isolated, flakes in the full run).
[Collection("NamingStandardSingleton")]
public class ApprovalBlockingRuleGateTests
{
    #region Fakes (public: the C# dynamic binder resolves them cross-assembly)

    public sealed class FakeProp
    {
        public FakeProp(object value) { Value = value; }
        public object Value { get; }
    }

    /// <summary>
    /// SCAPI-ish object: known property codes return a value, everything else throws the
    /// erwin "not valid class id or class name" shape - the same behaviour a real entity
    /// shows for a property that is not on its class (sparse storage).
    /// </summary>
    public sealed class FakeObject
    {
        private readonly Dictionary<string, object> _props;
        public FakeObject(Dictionary<string, object> props) { _props = props; }
        public FakeProp Properties(string code)
        {
            if (_props.TryGetValue(code, out var v)) return new FakeProp(v);
            throw new InvalidOperationException(
                $"Model Properties Component ! {code} is not valid class id or class name for object or property");
        }
    }

    private static FakeObject Obj(params (string code, string value)[] props)
        => new(props.ToDictionary(p => p.code, p => (object)p.value));

    #endregion

    #region Rule builders

    private static NamingStandardRule Rule(
        int id,
        NamingRuleKind kind,
        string objectType = "TABLE",
        string propertyCode = "Physical_Name",
        bool blocks = true,
        bool active = true)
        => new()
        {
            Id = id,
            ObjectType = objectType,
            PropertyCode = propertyCode,
            PropertyDefId = string.IsNullOrEmpty(propertyCode) ? (int?)null : 53,
            RuleType = kind,
            IsActive = active,
            BlocksDdlApprovement = blocks,
            ApplyOn = RuleApplyOn.Both,
        };

    private static NamingStandardRule RegexpRule(int id, string pattern, string error = "Regex ihlali", bool blocks = true)
    {
        var r = Rule(id, NamingRuleKind.Regexp, blocks: blocks);
        r.RegexpPattern = pattern;
        r.ErrorMessage = error;
        return r;
    }

    private static NamingStandardRule PrefixRule(int id, string prefix, string error = "Prefix ihlali")
    {
        var r = Rule(id, NamingRuleKind.Prefix);
        r.Prefix = prefix;
        r.ErrorMessage = error;
        return r;
    }

    private static NamingStandardRule LengthRule(int id, string op, int? value)
    {
        var r = Rule(id, NamingRuleKind.Length);
        r.LengthOperator = op;
        r.LengthValue = value;
        r.ErrorMessage = "Uzunluk ihlali";
        return r;
    }

    private static void Seed(params NamingStandardRule[] rules)
        => NamingStandardService.Instance.SeedForTesting(rules);

    private static void ClearSeed()
        => NamingStandardService.Instance.SeedForTesting(Array.Empty<NamingStandardRule>());

    #endregion

    // ---------- Blocking-rule selection ----------

    [Fact]
    public void Only_rules_flagged_as_blocking_are_surfaced()
    {
        Seed(
            RegexpRule(1, "^[A-Z]+$", blocks: true),
            RegexpRule(2, "^[A-Z]+$", blocks: false),
            PrefixRule(3, "VP_"));
        try
        {
            NamingStandardService.Instance.GetApprovalBlockingRules()
                .Select(r => r.Id).Should().BeEquivalentTo(new[] { 1, 3 });
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Inactive_blocking_rule_is_not_surfaced()
    {
        var inactive = RegexpRule(9, "^[A-Z]+$");
        inactive.IsActive = false;
        Seed(inactive);
        try
        {
            NamingStandardService.Instance.GetApprovalBlockingRules().Should().BeEmpty();
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Seeded_cache_is_considered_current_for_its_config()
    {
        // SeedForTesting stamps the config id, so EnsureLoadedForConfig must not try to
        // reach a database from a unit test. -1 is what ActiveConfigId reports unbound.
        Seed(RegexpRule(1, "^[A-Z]+$"));
        try
        {
            NamingStandardService.Instance.LoadedConfigId.Should().Be(-1);
            NamingStandardService.Instance.EnsureLoadedForConfig(-1).Should().BeTrue();
        }
        finally { ClearSeed(); }
    }

    // ---------- Structural preflight (no silent pass) ----------

    [Fact]
    public void Template_rule_flagged_as_blocking_is_a_hard_failure()
    {
        // NamingValidationEngine treats Template as an explicit no-op (it is a generator,
        // not a validator), so a blocking Template rule would otherwise be a permanent
        // silent pass wearing the appearance of enforcement.
        var rule = Rule(10, NamingRuleKind.Template);
        rule.ValueTemplate = "{Physical_Name}_X";

        ApprovalBlockingRuleGate.GetUnevaluatableReason(rule)
            .Should().NotBeNull().And.Contain("Template");
    }

    [Fact]
    public void Uncompilable_regexp_is_a_hard_failure()
    {
        // EvaluateRule swallows a bad pattern to Debug.WriteLine and emits NO violation.
        ApprovalBlockingRuleGate.GetUnevaluatableReason(RegexpRule(11, "^[A-Z"))
            .Should().NotBeNull().And.Contain("invalid pattern");
    }

    [Fact]
    public void Empty_regexp_pattern_is_a_hard_failure()
    {
        ApprovalBlockingRuleGate.GetUnevaluatableReason(RegexpRule(12, ""))
            .Should().NotBeNull().And.Contain("no pattern");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_prefix_value_is_a_hard_failure(string? prefix)
    {
        var rule = Rule(13, NamingRuleKind.Prefix);
        rule.Prefix = prefix;

        ApprovalBlockingRuleGate.GetUnevaluatableReason(rule)
            .Should().NotBeNull().And.Contain("no prefix value");
    }

    [Fact]
    public void Empty_suffix_value_is_a_hard_failure()
    {
        var rule = Rule(14, NamingRuleKind.Suffix);
        rule.Suffix = "";

        ApprovalBlockingRuleGate.GetUnevaluatableReason(rule)
            .Should().NotBeNull().And.Contain("no suffix value");
    }

    [Fact]
    public void Length_rule_without_operand_is_a_hard_failure()
    {
        ApprovalBlockingRuleGate.GetUnevaluatableReason(LengthRule(15, "<=", null))
            .Should().NotBeNull().And.Contain("operator");
    }

    [Fact]
    public void Length_rule_with_unsupported_operator_is_a_hard_failure()
    {
        // EvaluateRule's operator switch falls through to `_ => true` (= no violation) for
        // anything it does not know, which is a silent pass.
        ApprovalBlockingRuleGate.GetUnevaluatableReason(LengthRule(16, "!=", 30))
            .Should().NotBeNull().And.Contain("unsupported operator");
    }

    [Fact]
    public void Unmappable_object_type_is_a_hard_failure()
    {
        // DOMAIN has no entry in the admin-OBJECT_TYPE -> SCAPI-class map, so there is
        // nothing to walk and the rule can never be checked.
        var rule = RegexpRule(17, "^[A-Z]+$");
        rule.ObjectType = "DOMAIN";

        ApprovalBlockingRuleGate.GetUnevaluatableReason(rule)
            .Should().NotBeNull().And.Contain("DOMAIN");
    }

    [Fact]
    public void Propertyless_rule_is_only_valid_for_the_Required_type()
    {
        // PropertyCode "" means "an object of this type must exist" - Required only.
        var existence = Rule(18, NamingRuleKind.Required, propertyCode: "");
        ApprovalBlockingRuleGate.GetUnevaluatableReason(existence).Should().BeNull();

        var bogus = Rule(19, NamingRuleKind.Regexp, propertyCode: "");
        bogus.RegexpPattern = "^[A-Z]+$";
        ApprovalBlockingRuleGate.GetUnevaluatableReason(bogus)
            .Should().NotBeNull().And.Contain("only the Required type supports");
    }

    [Fact]
    public void Dangling_udp_condition_is_a_hard_failure()
    {
        // The loader's LEFT JOIN yields a NULL name when the gating UDP was deleted in
        // admin while the condition row survived. The engine treats such a term as
        // "does not match", so the rule would apply to NOTHING and pass silently -
        // indistinguishable from a clean model.
        var rule = RegexpRule(25, "^[A-Z_]+$");
        rule.Conditions.Add(new NamingRuleCondition
        {
            OrderIndex = 0,
            DependsOnUdpId = 77,
            DependsOnUdpName = "",     // deleted definition
            DependsOnPropertyValues = "Log",
        });

        ApprovalBlockingRuleGate.GetUnevaluatableReason(rule)
            .Should().NotBeNull().And.Contain("77");
    }

    [Fact]
    public void Dangling_property_condition_is_a_hard_failure()
    {
        var rule = RegexpRule(26, "^[A-Z_]+$");
        rule.Conditions.Add(new NamingRuleCondition
        {
            OrderIndex = 0,
            DependsOnPropertyDefId = 88,
            DependsOnPropertyCode = "",   // deleted property
            DependsOnPropertyValues = "X",
        });

        ApprovalBlockingRuleGate.GetUnevaluatableReason(rule)
            .Should().NotBeNull().And.Contain("88");
    }

    [Fact]
    public void Intact_condition_passes_the_preflight()
    {
        var rule = RegexpRule(27, "^[A-Z_]+$");
        rule.Conditions.Add(new NamingRuleCondition
        {
            OrderIndex = 0,
            DependsOnUdpId = 77,
            DependsOnUdpName = "TableClass",
            DependsOnPropertyValues = "Log",
        });

        ApprovalBlockingRuleGate.GetUnevaluatableReason(rule).Should().BeNull();
    }

    [Fact]
    public void Well_formed_rules_pass_the_preflight()
    {
        ApprovalBlockingRuleGate.GetUnevaluatableReason(RegexpRule(20, "^[A-Z_]+$")).Should().BeNull();
        ApprovalBlockingRuleGate.GetUnevaluatableReason(PrefixRule(21, "VP_")).Should().BeNull();
        ApprovalBlockingRuleGate.GetUnevaluatableReason(LengthRule(22, "<=", 30)).Should().BeNull();
        ApprovalBlockingRuleGate.GetUnevaluatableReason(Rule(23, NamingRuleKind.Required)).Should().BeNull();
    }

    [Fact]
    public void ApplyOn_does_not_affect_the_preflight()
    {
        // Decision 2026-07-25: the gate ignores APPLY_ON entirely, so a Create-scoped
        // blocking rule must still be evaluated (never skipped, never rejected).
        foreach (var applyOn in new[] { RuleApplyOn.Create, RuleApplyOn.Update, RuleApplyOn.Both })
        {
            var rule = RegexpRule(24, "^[A-Z_]+$");
            rule.ApplyOn = applyOn;
            ApprovalBlockingRuleGate.GetUnevaluatableReason(rule).Should().BeNull($"ApplyOn={applyOn} must not gate the check");
        }
    }

    // ---------- Object-type mapping ----------

    [Theory]
    [InlineData("TABLE", "Entity")]
    [InlineData("COLUMN", "Attribute")]
    [InlineData("VIEW", "View")]
    [InlineData("INDEX", "Key_Group")]
    [InlineData("PRIMARY_KEY", "Key_Group")]
    [InlineData("PRIMARY KEY", "Key_Group")]
    [InlineData("SUBJECT AREA", "Subject_Area")]
    [InlineData("MODEL", "Model")]
    [InlineData("model", "Model")]
    public void Object_types_resolve_to_an_inspectable_class(string objectType, string expected)
    {
        ApprovalBlockingRuleGate.ScapiClassFor(objectType).Should().Be(expected);
    }

    [Theory]
    [InlineData("DOMAIN")]
    [InlineData("PROCEDURE")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_object_types_have_no_class(string? objectType)
    {
        ApprovalBlockingRuleGate.ScapiClassFor(objectType).Should().BeNull();
    }

    [Theory]
    [InlineData("PRIMARY KEY", true)]
    [InlineData("primary_key", true)]
    [InlineData("TABLE", false)]
    public void PrimaryKey_object_type_is_recognised_in_both_spellings(string objectType, bool expected)
    {
        ApprovalBlockingRuleGate.IsPrimaryKeyObjectType(objectType).Should().Be(expected);
    }

    [Theory]
    [InlineData(">=", true)]
    [InlineData("<=", true)]
    [InlineData(">", true)]
    [InlineData("<", true)]
    [InlineData("=", true)]
    [InlineData("!=", false)]
    [InlineData("<>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_operators_the_engine_implements_are_supported(string? op, bool expected)
    {
        ApprovalBlockingRuleGate.IsSupportedLengthOperator(op).Should().Be(expected);
    }

    // ---------- Rule summaries ----------

    [Fact]
    public void Rule_summaries_render_the_payload()
    {
        ApprovalBlockingRuleGate.DescribeRule(PrefixRule(30, "VP_")).Should().Be("Prefix 'VP_'");
        ApprovalBlockingRuleGate.DescribeRule(LengthRule(31, "<=", 30)).Should().Be("Length <= 30");
        ApprovalBlockingRuleGate.DescribeRule(RegexpRule(32, "^[A-Z]+$")).Should().Be("Regexp '^[A-Z]+$'");
        ApprovalBlockingRuleGate.DescribeRule(Rule(33, NamingRuleKind.Required)).Should().Be("Required");
        ApprovalBlockingRuleGate.DescribeRule(Rule(34, NamingRuleKind.Required, propertyCode: ""))
            .Should().Be("Required (object must exist)");
    }

    // ---------- Atomic rule-vs-object verdict ----------

    [Fact]
    public void Violation_carries_the_admin_error_message_verbatim()
    {
        // Admin messages are routinely Turkish and must reach the report untranslated.
        var rule = RegexpRule(40, "^[A-Z_]+$", error: "Tablo adi sadece buyuk harf olmali");
        Seed(rule);
        try
        {
            var verdict = ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(rule, "musteri", Obj());

            verdict.Applicable.Should().BeTrue();
            verdict.HasEvaluationError.Should().BeFalse();
            verdict.Violations.Should().ContainSingle()
                .Which.Should().Be("Tablo adi sadece buyuk harf olmali");
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Compliant_value_produces_no_violation()
    {
        var rule = RegexpRule(41, "^[A-Z_]+$");
        Seed(rule);
        try
        {
            var verdict = ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(rule, "MUSTERI", Obj());

            verdict.Applicable.Should().BeTrue();
            verdict.Violations.Should().BeEmpty();
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Create_scoped_rule_still_blocks_an_existing_object()
    {
        // The load-bearing consequence of ignoring APPLY_ON: a rule scoped to Create is
        // still enforced by the gate. Passing isNew=false (the natural framing for a
        // whole-model sweep) would make this rule incapable of ever blocking.
        var rule = RegexpRule(42, "^[A-Z_]+$", error: "buyuk harf");
        rule.ApplyOn = RuleApplyOn.Create;
        Seed(rule);
        try
        {
            var verdict = ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(rule, "musteri", Obj());

            verdict.Violations.Should().ContainSingle().Which.Should().Be("buyuk harf");
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Update_scoped_rule_is_also_enforced()
    {
        var rule = RegexpRule(43, "^[A-Z_]+$", error: "buyuk harf");
        rule.ApplyOn = RuleApplyOn.Update;
        Seed(rule);
        try
        {
            ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(rule, "musteri", Obj())
                .Violations.Should().ContainSingle();
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Rule_whose_condition_does_not_match_is_skipped_not_violated()
    {
        var rule = RegexpRule(44, "^[A-Z_]+$");
        rule.Conditions.Add(new NamingRuleCondition
        {
            OrderIndex = 0,
            DependsOnUdpId = 7,
            DependsOnUdpName = "TableClass",
            DependsOnPropertyValues = "Log",
        });
        Seed(rule);
        try
        {
            var applies = ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(
                rule, "musteri", Obj(("Entity.Physical.TableClass", "Log")));
            var doesNot = ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(
                rule, "musteri", Obj(("Entity.Physical.TableClass", "History")));

            applies.Applicable.Should().BeTrue();
            applies.Violations.Should().ContainSingle();

            doesNot.Applicable.Should().BeFalse();
            doesNot.Violations.Should().BeEmpty();
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Pk_membership_is_taken_from_the_caller_not_from_a_property_read()
    {
        // erwin exposes no readable PK-membership property on an Attribute, so the gate
        // resolves it from the Key_Group graph and hands it in. Without that, the
        // condition would read empty and the rule would silently never apply.
        var rule = RegexpRule(45, "^[A-Z_]+$", error: "PK kolon adi");
        rule.ObjectType = "COLUMN";
        rule.Conditions.Add(new NamingRuleCondition
        {
            OrderIndex = 0,
            DependsOnPropertyDefId = 91,
            DependsOnPropertyCode = "IsPrimaryKey",
            DependsOnPropertyValues = "True",
        });
        Seed(rule);
        try
        {
            var pkColumn = ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(rule, "musteri_id", Obj(), pkMembership: true);
            var plainColumn = ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(rule, "musteri_id", Obj(), pkMembership: false);

            pkColumn.Applicable.Should().BeTrue();
            pkColumn.Violations.Should().ContainSingle();

            plainColumn.Applicable.Should().BeFalse();
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Required_rule_flags_an_empty_value()
    {
        var rule = Rule(46, NamingRuleKind.Required, propertyCode: "Definition");
        rule.ErrorMessage = "Aciklama zorunlu";
        Seed(rule);
        try
        {
            ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(rule, "", Obj())
                .Violations.Should().ContainSingle().Which.Should().Be("Aciklama zorunlu");

            ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(rule, "dolu", Obj())
                .Violations.Should().BeEmpty();
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Stacked_prefixes_in_canonical_form_do_not_block()
    {
        // Two prefix rules on one property: the composed name 'PF_VP_ABC' carries both,
        // but the inner rule's StartsWith can never pass. Blocking a submission on that
        // unsatisfiable positional violation would be a false block, so the canonical-form
        // check (mirroring ValidateObjectName) must drop it.
        var inner = PrefixRule(47, "VP_");
        var outer = PrefixRule(48, "PF_");
        outer.SortOrder = 2;
        Seed(inner, outer);
        try
        {
            ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(inner, "PF_VP_ABC", Obj())
                .Violations.Should().BeEmpty("'PF_VP_ABC' already carries every applicable affix");

            ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(outer, "PF_VP_ABC", Obj())
                .Violations.Should().BeEmpty();
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Genuinely_missing_prefix_still_blocks()
    {
        var rule = PrefixRule(49, "VP_", error: "VP_ ile baslamali");
        Seed(rule);
        try
        {
            ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(rule, "ABC", Obj())
                .Violations.Should().ContainSingle().Which.Should().Be("VP_ ile baslamali");
        }
        finally { ClearSeed(); }
    }

    [Theory]
    [InlineData(RuleApplyOn.Create)]
    [InlineData(RuleApplyOn.Update)]
    public void Affix_suppression_does_not_smuggle_ApplyOn_back_in(RuleApplyOn applyOn)
    {
        // REGRESSION (adversarial review 2026-07-25). The canonical-affix suppression asks
        // ApplyNamingStandards for the canonical name, and THAT method filters its affix
        // set by MatchesApplyOn(rule, isNew). Under the mismatched framing the rule is
        // dropped from the apply, the canonical form trivially equals the input, and the
        // suppression swallowed the violation - so every Prefix/Suffix blocking rule that
        // was not APPLY_ON='Both' silently stopped blocking. Exactly the silent pass the
        // ignore-APPLY_ON decision exists to prevent.
        var rule = PrefixRule(50, "VP_", error: "VP_ ile baslamali");
        rule.ApplyOn = applyOn;
        Seed(rule);
        try
        {
            ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(rule, "ABC", Obj())
                .Violations.Should().ContainSingle(
                    $"a missing prefix must block regardless of ApplyOn={applyOn}");
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Affix_suppression_does_not_swallow_a_pk_conditioned_violation()
    {
        // REGRESSION (adversarial review 2026-07-25). The gate resolves PK membership from
        // the Key_Group graph and hands it in, but ApplyNamingStandards re-resolves
        // conditions WITHOUT it (erwin exposes no PK property on an Attribute), so it
        // considered the rule not applicable, produced an unchanged canonical name, and the
        // suppression dropped a real violation on a PK column.
        var rule = PrefixRule(51, "PK_", error: "PK kolonu PK_ ile baslamali");
        rule.ObjectType = "COLUMN";
        rule.Conditions.Add(new NamingRuleCondition
        {
            OrderIndex = 0,
            DependsOnPropertyDefId = 91,
            DependsOnPropertyCode = "IsPrimaryKey",
            DependsOnPropertyValues = "True",
        });
        Seed(rule);
        try
        {
            ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(rule, "musteri_id", Obj(), pkMembership: true)
                .Violations.Should().ContainSingle();
        }
        finally { ClearSeed(); }
    }

    [Fact]
    public void Null_rule_is_an_evaluation_error_not_a_pass()
    {
        var verdict = ApprovalBlockingRuleGate.EvaluateRuleAgainstObject(null!, "X", Obj());

        verdict.HasEvaluationError.Should().BeTrue();
        verdict.Violations.Should().BeEmpty();
    }

    // ---------- Reporting ----------

    [Fact]
    public void Duplicate_issues_are_collapsed_preserving_order()
    {
        var a = new ApprovalBlockingIssue(ApprovalBlockingIssueKind.Violation, 1, "TABLE", "A", "Name", "Regexp 'x'", "bad");
        var aAgain = new ApprovalBlockingIssue(ApprovalBlockingIssueKind.Violation, 1, "TABLE", "A", "Name", "Regexp 'x'", "bad");
        var b = new ApprovalBlockingIssue(ApprovalBlockingIssueKind.Violation, 1, "TABLE", "B", "Name", "Regexp 'x'", "bad");

        var deduped = ApprovalBlockingRuleGate.Dedupe(new[] { a, aAgain, b });

        deduped.Should().HaveCount(2);
        deduped[0].ObjectName.Should().Be("A");
        deduped[1].ObjectName.Should().Be("B");
    }

    [Fact]
    public void Report_line_names_the_rule_the_object_and_the_reason()
    {
        var issue = new ApprovalBlockingIssue(
            ApprovalBlockingIssueKind.Violation, 1127, "COLUMN", "MUSTERI.ad", "Name", "Regexp '^[A-Z]+$'", "buyuk harf");

        string line = issue.ToReportLine();

        line.Should().Contain("rule#1127")
            .And.Contain("COLUMN")
            .And.Contain("MUSTERI.ad")
            .And.Contain("buyuk harf");
    }
}
