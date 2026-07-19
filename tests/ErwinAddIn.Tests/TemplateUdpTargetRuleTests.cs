using System;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Rule-selection coverage for the migration-9 UDP write target
/// (MC_NAMING_STANDARD.TARGET_UDP_ID): <see cref="NamingStandardService.GetTemplateRules"/>
/// must return Template rules that target EITHER a property (PropertyCode) OR
/// a UDP (TargetUdpId + resolved TargetUdpName), and must keep skipping rows
/// that target nothing. Shares the singleton-seeding collection with the other
/// NamingStandardService fixtures so parallel classes never race the cache.
/// </summary>
[Collection("NamingStandardSingleton")]
public class TemplateUdpTargetRuleTests
{
    private static NamingStandardRule Template(
        int id,
        string objectType = "COLUMN",
        string propertyCode = "",
        int? targetUdpId = null,
        string targetUdpName = "",
        string targetUdpObjectType = "",
        string template = "{Table.Physical_Name}",
        int sortOrder = 0)
        => new()
        {
            Id = id,
            ObjectType = objectType,
            PropertyCode = propertyCode,
            RuleType = NamingRuleKind.Template,
            TargetUdpId = targetUdpId,
            TargetUdpName = targetUdpName,
            TargetUdpObjectType = targetUdpObjectType,
            ValueTemplate = template,
            TemplateFillMode = "Always",
            IsActive = true,
            SortOrder = sortOrder,
        };

    [Fact]
    public void GetTemplateRules_returns_property_targeted_and_udp_targeted_rules()
    {
        try
        {
            NamingStandardService.Instance.SeedForTesting(new[]
            {
                Template(1, propertyCode: "Comment", sortOrder: 1),
                Template(2, targetUdpId: 42, targetUdpName: "SourceSystem", targetUdpObjectType: "COLUMN", sortOrder: 2),
            });

            var rules = NamingStandardService.Instance.GetTemplateRules("Column");

            rules.Should().HaveCount(2);
            rules[0].Id.Should().Be(1);
            rules[1].Id.Should().Be(2);
            rules[1].TargetUdpId.Should().Be(42);
            rules[1].TargetUdpName.Should().Be("SourceSystem");
        }
        finally { NamingStandardService.Instance.SeedForTesting(Array.Empty<NamingStandardRule>()); }
    }

    [Fact]
    public void GetTemplateRules_skips_rules_with_no_target_at_all()
    {
        // Neither PropertyCode nor TargetUdpId: nothing to write to. Also a
        // TargetUdpId whose NAME failed to resolve (deleted UDP definition)
        // must not surface - the applier would have no path to write.
        try
        {
            NamingStandardService.Instance.SeedForTesting(new[]
            {
                Template(1),                                           // no target
                Template(2, targetUdpId: 7, targetUdpName: ""),        // unresolved UDP name
                Template(3, targetUdpId: 8, targetUdpName: "Ok", targetUdpObjectType: "COLUMN"),
            });

            var rules = NamingStandardService.Instance.GetTemplateRules("Column");

            rules.Should().ContainSingle().Which.Id.Should().Be(3);
        }
        finally { NamingStandardService.Instance.SeedForTesting(Array.Empty<NamingStandardRule>()); }
    }

    [Fact]
    public void GetTemplateRules_still_requires_a_value_template()
    {
        try
        {
            NamingStandardService.Instance.SeedForTesting(new[]
            {
                Template(1, targetUdpId: 9, targetUdpName: "X", targetUdpObjectType: "COLUMN", template: ""),
            });

            NamingStandardService.Instance.GetTemplateRules("Column").Should().BeEmpty();
        }
        finally { NamingStandardService.Instance.SeedForTesting(Array.Empty<NamingStandardRule>()); }
    }
}
