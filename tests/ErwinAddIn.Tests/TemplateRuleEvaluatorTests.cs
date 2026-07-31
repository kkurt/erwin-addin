using System;
using System.Collections.Generic;
using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Contract for <see cref="TemplateRuleEvaluator"/>, the SCAPI-free decision half
/// of applying a Template naming rule.
///
/// <para>These exist because the two pre-existing appliers
/// (ApplyColumnTemplateRules / ApplyPrimaryKeyRules) inline the same seven-step
/// decision next to their COM walk and therefore have no unit coverage at all.
/// Every case below is the behaviour those two already implement; pinning it here
/// is what makes migrating them later a reviewable change rather than a hope.</para>
/// </summary>
public class TemplateRuleEvaluatorTests
{
    private const string ModelPrefix = "Model.Physical.";
    private const string ColumnPrefix = "Attribute.Physical.";
    private const string Hint = "{Current}_SUFFIX";

    private static NamingStandardRule UdpRule(
        int id = 1180,
        string objectType = "MODEL",
        string targetUdpName = "ApplicationCode",
        string targetUdpObjectType = "MODEL",
        string template = "{Udp:Application}",
        string fillMode = "Always",
        string errorMessage = "") => new()
    {
        Id = id,
        ObjectType = objectType,
        PropertyCode = "",
        RuleType = NamingRuleKind.Template,
        TargetUdpId = 2,
        TargetUdpName = targetUdpName,
        TargetUdpObjectType = targetUdpObjectType,
        ValueTemplate = template,
        TemplateFillMode = fillMode,
        ErrorMessage = errorMessage,
        IsActive = true,
    };

    private static TemplateRuleDecision Evaluate(
        NamingStandardRule rule,
        string prefix = ModelPrefix,
        string currentValue = "",
        Func<string, string>? render = null,
        List<string>? reads = null)
    {
        return TemplateRuleEvaluator.Evaluate(
            rule,
            prefix,
            targetCode => { reads?.Add(targetCode); return currentValue; },
            render ?? (_ => "RENDERED"),
            Hint);
    }

    [Theory]
    [InlineData(ModelPrefix, "Model.Physical.ApplicationCode")]
    [InlineData(ColumnPrefix, "Attribute.Physical.ApplicationCode")]
    [InlineData("Key_Group.Physical.", "Key_Group.Physical.ApplicationCode")]
    public void Udp_target_path_is_composed_from_the_caller_supplied_prefix(string prefix, string expected)
    {
        // One seam replaces three hardcoded prefixes. Pinned across all three so a
        // later migration of the Column / PK appliers cannot silently change a path.
        var rule = UdpRule(objectType: "MODEL", targetUdpObjectType: "MODEL");
        var decision = Evaluate(rule, prefix);

        decision.Action.Should().Be(TemplateRuleAction.Write);
        decision.TargetCode.Should().Be(expected);
        decision.TargetsUdp.Should().BeTrue();
    }

    [Fact]
    public void Target_udp_owned_by_another_object_type_is_skipped_without_reading_the_model()
    {
        // A MODEL-scoped UDP must never be filled from a per-column pass: it would
        // rewrite the model value on every column change.
        var reads = new List<string>();
        var rule = UdpRule(objectType: "COLUMN", targetUdpObjectType: "MODEL");

        var decision = Evaluate(rule, ColumnPrefix, reads: reads);

        decision.Action.Should().Be(TemplateRuleAction.Skip);
        decision.Reason.Should().Contain("MODEL").And.Contain("COLUMN");
        reads.Should().BeEmpty("a rule that can never fire must not cost a SCAPI read");
    }

    [Fact]
    public void Unresolved_token_is_skipped_with_the_rules_error_message_AND_the_failing_token()
    {
        // Rule 1181 ({Udp:Application|split:\|:1}) hits this whenever the source UDP
        // holds no pipe. It must land as a logged skip, never as an exception on the
        // heartbeat thread.
        //
        // Both halves are required: the admin ERROR_MESSAGE says what the modeller
        // should do, the token says which part of the template broke. Reporting only
        // the first leaves a support engineer with an instruction and no defect.
        var rule = UdpRule(errorMessage: "Application UDP must be 'CODE | NAME'.");

        var decision = Evaluate(rule, render: _ => throw new TemplateResolutionException(
            "Udp:Application|split:\\|:1", "function chain produced an empty value"));

        decision.Action.Should().Be(TemplateRuleAction.Skip);
        decision.Reason.Should().StartWith("Application UDP must be 'CODE | NAME'.");
        decision.Reason.Should().Contain("Udp:Application|split:\\|:1");
        decision.Reason.Should().Contain("function chain produced an empty value");
    }

    [Fact]
    public void Unresolved_token_without_an_error_message_still_names_the_failing_token()
    {
        var decision = Evaluate(UdpRule(errorMessage: ""), render: _ => throw new TemplateResolutionException(
            "Udp:Application", "resolved to an empty value"));

        decision.Action.Should().Be(TemplateRuleAction.Skip);
        decision.Reason.Should().Contain("Udp:Application");
    }

    [Fact]
    public void Identical_rendered_value_reports_NoChange_rather_than_Write()
    {
        // The steady state of a healthy rule. Distinguished from Skip so the applier
        // stays quiet, and from Write so the model is never dirtied by a no-op.
        var decision = Evaluate(UdpRule(), currentValue: "UYG051", render: _ => "UYG051");

        decision.Action.Should().Be(TemplateRuleAction.NoChange);
    }

    [Fact]
    public void A_single_trailing_space_is_still_a_change()
    {
        // Ordinal comparison, deliberately: rules 1180/1181 render a trailing space
        // today, and treating that as "no change" would hide the authoring defect.
        var decision = Evaluate(UdpRule(), currentValue: "UYG051", render: _ => "UYG051 ");

        decision.Action.Should().Be(TemplateRuleAction.Write);
        decision.RenderedValue.Should().Be("UYG051 ");
    }

    [Fact]
    public void OnlyIfEmpty_does_not_overwrite_a_non_empty_target()
    {
        var decision = Evaluate(UdpRule(fillMode: "OnlyIfEmpty"), currentValue: "hand written");

        decision.Action.Should().Be(TemplateRuleAction.Skip);
        decision.Reason.Should().Contain("OnlyIfEmpty").And.Contain("hand written");
    }

    [Fact]
    public void OnlyIfEmpty_writes_into_an_empty_target()
    {
        var decision = Evaluate(UdpRule(fillMode: "OnlyIfEmpty"), currentValue: "");

        decision.Action.Should().Be(TemplateRuleAction.Write);
    }

    [Fact]
    public void Always_overwrites_a_non_empty_target()
    {
        var decision = Evaluate(UdpRule(fillMode: "Always"), currentValue: "stale");

        decision.Action.Should().Be(TemplateRuleAction.Write);
        decision.CurrentValue.Should().Be("stale", "the applier shows current -> new in its prompt");
    }

    [Fact]
    public void Unknown_fill_mode_is_skipped_and_never_guessed()
    {
        var decision = Evaluate(UdpRule(fillMode: "SometimesMaybe"), currentValue: "x");

        decision.Action.Should().Be(TemplateRuleAction.Skip);
        decision.Reason.Should().Contain("SometimesMaybe");
    }

    [Fact]
    public void Unknown_fill_mode_is_caught_before_the_read_and_the_render()
    {
        // It is a property of the ROW, so it belongs with the structural guards. If it
        // ran after the render, a template that ALSO fails to resolve would report the
        // token failure and hide the real defect, which is the malformed fill mode.
        var reads = new List<string>();

        var decision = Evaluate(
            UdpRule(fillMode: "SometimesMaybe"),
            reads: reads,
            render: _ => throw new TemplateResolutionException("Udp:Whatever", "resolved to an empty value"));

        decision.Action.Should().Be(TemplateRuleAction.Skip);
        decision.Reason.Should().Contain("SometimesMaybe").And.NotContain("Whatever");
        reads.Should().BeEmpty();
    }

    [Fact]
    public void Self_referential_udp_template_is_refused_before_any_read()
    {
        // {Udp:ApplicationCode} writing ApplicationCode grows without bound under
        // FILL_MODE=Always. The hint tells the author to use {Current} instead.
        var reads = new List<string>();
        var rule = UdpRule(targetUdpName: "ApplicationCode", template: "PRE_{Udp:ApplicationCode}");

        var decision = Evaluate(rule, reads: reads);

        decision.Action.Should().Be(TemplateRuleAction.Skip);
        decision.Reason.Should().Contain("self-referential").And.Contain(Hint);
        reads.Should().BeEmpty();
    }

    [Fact]
    public void Current_token_with_OnlyIfEmpty_is_refused()
    {
        var rule = UdpRule(template: "{Current}_X", fillMode: "OnlyIfEmpty");

        var decision = Evaluate(rule);

        decision.Action.Should().Be(TemplateRuleAction.Skip);
        decision.Reason.Should().Contain("{Current}").And.Contain("OnlyIfEmpty");
    }

    [Fact]
    public void A_rule_with_neither_a_property_nor_a_udp_target_is_skipped()
    {
        var rule = UdpRule();
        rule.TargetUdpId = null;
        rule.TargetUdpName = "";
        rule.PropertyCode = "";

        var decision = Evaluate(rule);

        decision.Action.Should().Be(TemplateRuleAction.Skip);
        decision.Reason.Should().Contain("neither");
    }

    [Fact]
    public void A_property_targeted_rule_uses_the_property_code_verbatim()
    {
        var rule = UdpRule();
        rule.TargetUdpId = null;
        rule.TargetUdpName = "";
        rule.PropertyCode = "Definition";

        var decision = Evaluate(rule);

        decision.Action.Should().Be(TemplateRuleAction.Write);
        decision.TargetCode.Should().Be("Definition");
        decision.TargetsUdp.Should().BeFalse();
    }

    [Fact]
    public void The_target_is_read_exactly_once()
    {
        // {Current} reshapes this value and both the fill mode and the idempotency
        // check need it, but a SCAPI read is expensive enough to pin at one.
        var reads = new List<string>();

        Evaluate(UdpRule(), reads: reads);

        reads.Should().ContainSingle().Which.Should().Be("Model.Physical.ApplicationCode");
    }

    [Fact]
    public void A_udp_targeted_rule_without_a_path_prefix_is_a_programming_error()
    {
        // Fail fast rather than composing ".ApplicationCode" and writing to nowhere.
        Action act = () => Evaluate(UdpRule(), prefix: "");

        act.Should().Throw<ArgumentException>().WithParameterName("udpPathPrefix");
    }
}
