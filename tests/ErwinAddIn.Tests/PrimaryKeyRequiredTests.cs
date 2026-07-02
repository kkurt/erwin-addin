using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Decision coverage for the PER-TABLE "Required PRIMARY KEY" enforcement
/// (<see cref="TableTypeMonitorService.ComputePkRequirementWarning"/>). A
/// Required-PRIMARY-KEY rule means every table must own a PK (a Key_Group with
/// Key_Group_Type == "PK"), not merely that the model has one somewhere; the warning
/// must honour APPLY_ON (Create / Update / Both) against the table's new/changed
/// status and only fire when the table has no PK. The SCAPI Key_Group walk and the
/// dialog are integration plumbing; this pins the pure decision.
/// </summary>
public class PrimaryKeyRequiredTests
{
    private static NamingStandardRule PkRule(RuleApplyOn applyOn, string msg = "PK Olmak zorunda!!!")
        => new()
        {
            Id = 1168,
            ObjectType = "PRIMARY KEY",
            PropertyCode = "",            // existence rule: no target property
            RuleType = NamingRuleKind.Required,
            IsActive = true,
            ApplyOn = applyOn,
            ErrorMessage = msg,
        };

    private static List<NamingStandardRule> Rules(params NamingStandardRule[] r) => new(r);

    [Theory]
    // apply=Both: warns whenever the table has no PK, regardless of new/changed.
    [InlineData(RuleApplyOn.Both, true, false, true)]
    [InlineData(RuleApplyOn.Both, false, false, true)]
    // apply=Both + table already has a PK: never warns.
    [InlineData(RuleApplyOn.Both, true, true, false)]
    [InlineData(RuleApplyOn.Both, false, true, false)]
    // apply=Create: only new tables.
    [InlineData(RuleApplyOn.Create, true, false, true)]
    [InlineData(RuleApplyOn.Create, false, false, false)]
    // apply=Update: only existing/changed tables.
    [InlineData(RuleApplyOn.Update, false, false, true)]
    [InlineData(RuleApplyOn.Update, true, false, false)]
    public void Warns_per_applyOn_and_pk_presence(RuleApplyOn applyOn, bool isNew, bool hasPk, bool expectWarn)
    {
        var body = TableTypeMonitorService.ComputePkRequirementWarning(
            Rules(PkRule(applyOn)), isNew, hasPk, "Vpaslan");

        if (expectWarn) body.Should().Be("PK Olmak zorunda!!!");
        else body.Should().BeNull();
    }

    [Fact]
    public void NonPrimaryKey_existence_rule_is_ignored()
    {
        // A Subject Area existence rule must NOT be treated as a per-table PK rule.
        var subjectArea = new NamingStandardRule
        {
            Id = 99,
            ObjectType = "Subject Area",
            PropertyCode = "",
            RuleType = NamingRuleKind.Required,
            IsActive = true,
            ApplyOn = RuleApplyOn.Both,
            ErrorMessage = "Need a subject area",
        };

        TableTypeMonitorService.ComputePkRequirementWarning(
            Rules(subjectArea), false, false, "Vpaslan").Should().BeNull();
    }

    [Fact]
    public void Empty_rule_set_returns_null()
    {
        TableTypeMonitorService.ComputePkRequirementWarning(
            new List<NamingStandardRule>(), false, false, "Vpaslan").Should().BeNull();
    }

    [Fact]
    public void Blank_message_falls_back_to_default_english_body()
    {
        var body = TableTypeMonitorService.ComputePkRequirementWarning(
            Rules(PkRule(RuleApplyOn.Both, msg: "")), false, false, "Vpaslan");

        body.Should().Be("Table 'Vpaslan' must have a primary key.");
    }

    [Fact]
    public void Multiple_pk_rules_consolidate_distinct_messages()
    {
        var body = TableTypeMonitorService.ComputePkRequirementWarning(
            Rules(PkRule(RuleApplyOn.Both, "msg A"), PkRule(RuleApplyOn.Both, "msg B")),
            isNew: false, hasPk: false, tableName: "Vpaslan");

        body.Should().Contain("msg A").And.Contain("msg B");
    }

    [Fact]
    public void ObjectType_match_is_space_and_case_insensitive()
    {
        var underscore = PkRule(RuleApplyOn.Both);
        underscore.ObjectType = "primary_key";

        TableTypeMonitorService.ComputePkRequirementWarning(
            Rules(underscore), false, false, "Vpaslan").Should().Be("PK Olmak zorunda!!!");
    }
}
