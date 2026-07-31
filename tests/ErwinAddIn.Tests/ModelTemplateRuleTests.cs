using System;
using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Regression cover for the 2026-07-31 report: two live <c>OBJECT_TYPE=MODEL</c>
/// Template rules (MetaRepoZeynep config 1012, ids 1180/1181) loaded correctly,
/// were printed in the connect-time rule dump, and then never ran - because no
/// applier in the add-in asked for MODEL rules.
///
/// <para>The rules below are the production rows verbatim. What each test pins:
/// the selector really does return MODEL rules (so the applier is the only thing
/// that was missing), the object-type filter still isolates the three appliers
/// from one another, APPLY_ON=Create can never match a model, and the coverage
/// registry knows exactly which object types have an applier.</para>
///
/// <para>Shares the singleton-seeding collection with the other
/// NamingStandardService fixtures so parallel classes never race the cache.</para>
/// </summary>
[Collection("NamingStandardSingleton")]
public class ModelTemplateRuleTests
{
    /// <summary>Rule 1180 as it exists in MetaRepoZeynep config 1012.</summary>
    private static NamingStandardRule Rule1180() => new()
    {
        Id = 1180,
        ObjectType = "MODEL",
        PropertyCode = "",              // PROPERTY_DEF_ID is NULL: this is a UDP-targeted rule
        RuleType = NamingRuleKind.Template,
        TargetUdpId = 2,
        TargetUdpName = "ApplicationCode",
        TargetUdpObjectType = "MODEL",
        ValueTemplate = @"{Udp:Application|split:\|:0}",
        TemplateFillMode = "Always",
        ApplyOn = RuleApplyOn.Both,
        AutoApply = true,
        IsActive = true,
        SortOrder = 0,
    };

    /// <summary>Rule 1181 as it exists in MetaRepoZeynep config 1012.</summary>
    private static NamingStandardRule Rule1181() => new()
    {
        Id = 1181,
        ObjectType = "MODEL",
        PropertyCode = "",
        RuleType = NamingRuleKind.Template,
        TargetUdpId = 22,
        TargetUdpName = "ApplicationName",
        TargetUdpObjectType = "MODEL",
        ValueTemplate = @"{Udp:Application|split:\|:1}",
        TemplateFillMode = "Always",
        ApplyOn = RuleApplyOn.Both,
        AutoApply = true,
        IsActive = true,
        SortOrder = 1,
    };

    [Fact]
    public void GetTemplateRules_returns_the_udp_targeted_MODEL_rules()
    {
        // THE regression guard. An empty PropertyCode locks these rules out of every
        // other accessor on the service (GetPropertyCodes and
        // GetByObjectTypeAndProperty both filter it), so GetTemplateRules is the
        // only door they can come through.
        try
        {
            NamingStandardService.Instance.SeedForTesting(new[] { Rule1180(), Rule1181() });

            var rules = NamingStandardService.Instance.GetTemplateRules("MODEL");

            rules.Should().HaveCount(2);
            rules[0].Id.Should().Be(1180);
            rules[0].TargetUdpName.Should().Be("ApplicationCode");
            rules[1].Id.Should().Be(1181);
            rules[1].TargetUdpName.Should().Be("ApplicationName");
        }
        finally { NamingStandardService.Instance.SeedForTesting(Array.Empty<NamingStandardRule>()); }
    }

    [Fact]
    public void GetTemplateRules_matches_the_object_type_case_insensitively()
    {
        // The applier passes TemplateApplierRegistry.Model ("MODEL"); the admin row
        // could equally say "Model". Neither spelling may change the answer.
        try
        {
            NamingStandardService.Instance.SeedForTesting(new[] { Rule1180() });

            NamingStandardService.Instance.GetTemplateRules("MODEL").Should().HaveCount(1);
            NamingStandardService.Instance.GetTemplateRules("Model").Should().HaveCount(1);
        }
        finally { NamingStandardService.Instance.SeedForTesting(Array.Empty<NamingStandardRule>()); }
    }

    [Theory]
    [InlineData("Column")]
    [InlineData("PRIMARY KEY")]
    public void MODEL_rules_are_invisible_to_the_other_appliers(string otherObjectType)
    {
        // The per-column and per-PK passes must never see a MODEL rule: writing a
        // model-scoped value from a per-object walk would rewrite it on every edit.
        try
        {
            NamingStandardService.Instance.SeedForTesting(new[] { Rule1180(), Rule1181() });

            NamingStandardService.Instance.GetTemplateRules(otherObjectType).Should().BeEmpty();
        }
        finally { NamingStandardService.Instance.SeedForTesting(Array.Empty<NamingStandardRule>()); }
    }

    [Theory]
    [InlineData(RuleApplyOn.Both, true)]
    [InlineData(RuleApplyOn.Update, true)]
    [InlineData(RuleApplyOn.Create, false)]
    public void ApplyOn_Create_can_never_match_a_MODEL_rule(RuleApplyOn applyOn, bool expected)
    {
        // A model is OPENED, never created by the add-in, so isNew is false at every
        // MODEL evaluation moment. The applier reports Create loudly instead of
        // dropping it, which is what this pins.
        var rule = Rule1180();
        rule.ApplyOn = applyOn;

        NamingValidationEngine.MatchesApplyOn(rule, isNew: false).Should().Be(expected);
    }

    [Theory]
    [InlineData("MODEL", true)]
    [InlineData("Model", true)]
    [InlineData("Column", true)]
    [InlineData("COLUMN", true)]
    [InlineData("PRIMARY KEY", true)]
    [InlineData("Primary Key", true)]
    [InlineData("PRIMARY_KEY", true)]
    [InlineData("TABLE", false)]
    [InlineData("VIEW", false)]
    [InlineData("SUBJECT AREA", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_applier_registry_knows_which_object_types_actually_run(string? objectType, bool expected)
    {
        // Drives the connect-time "[TEMPLATE-SELFCHECK] ... will NEVER run" warning.
        // TABLE and VIEW are false on purpose: they have naming rules but no Template
        // applier, and that is exactly what the warning has to say out loud.
        TemplateApplierRegistry.HasApplier(objectType).Should().Be(expected);
    }

    [Fact]
    public void The_registry_constants_are_the_literals_the_appliers_pass()
    {
        // If someone renames a constant without updating the applier (or vice versa)
        // the selector silently returns nothing, which is this whole bug again.
        TemplateApplierRegistry.Model.Should().Be("MODEL");
        TemplateApplierRegistry.Column.Should().Be("Column");
        TemplateApplierRegistry.PrimaryKey.Should().Be("PRIMARY KEY");
        TemplateApplierRegistry.SupportedObjectTypes.Should().HaveCount(3);
    }
}
