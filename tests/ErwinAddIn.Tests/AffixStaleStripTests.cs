using System;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Idempotent-affix apply must not strip a coincidental, user-typed affix.
/// <para>
/// 2026-07-07 bug: adding column "AbcDate" was silently renamed to "Abc" - a
/// CONDITIONAL Suffix='Date' rule (live rule#1032) that did NOT apply to the column
/// (its condition was not met) treated the user's meaningful "Date" as a stale rule
/// decoration and stripped it. Fix: on a NEW object (<c>isNew</c>) a non-applicable
/// rule's affix can never be stale (no rule has ever applied to it), so it is left
/// alone; on an EXISTING object the stale-strip (removing an affix that became
/// obsolete after a conditioning-UDP flip) is preserved.
/// </para>
/// </summary>
[Collection("NamingStandardSingleton")]
public sealed class AffixStaleStripTests : IDisposable
{
    public void Dispose() =>
        NamingStandardService.Instance.SeedForTesting(Array.Empty<NamingStandardRule>());

    // A conditional Suffix='Date' rule. With a null SCAPI object the condition cannot
    // be read, so IsRuleApplicable returns false (non-applicable) - exactly the live
    // rule#1032 state for a column whose condition is not met.
    private static NamingStandardRule ConditionalDateSuffix()
    {
        var r = new NamingStandardRule
        {
            Id = 1032,
            ObjectType = "Column",
            PropertyCode = "Physical_Name",
            RuleType = NamingRuleKind.Suffix,
            Suffix = "Date",
            IsActive = true,
            AutoApply = true,
            ApplyOn = RuleApplyOn.Both,
        };
        r.Conditions.Add(new NamingRuleCondition
        {
            OrderIndex = 0,
            DependsOnUdpId = 101,
            DependsOnUdpName = "SomeUdp",
            DependsOnPropertyValues = "X",
        });
        return r;
    }

    [Fact]
    public void New_column_keeps_user_typed_affix_matching_a_nonapplicable_rule()
    {
        NamingStandardService.Instance.SeedForTesting(new[] { ConditionalDateSuffix() });

        // isNew=true: no rule has ever applied, so the trailing "Date" is user intent.
        NamingValidationEngine
            .ApplyNamingStandards("Column", "AbcDate", scapiObject: null, autoOnly: false, isNew: true)
            .Should().Be("AbcDate");
    }

    [Fact]
    public void Existing_column_still_strips_stale_affix_of_a_nonapplicable_rule()
    {
        NamingStandardService.Instance.SeedForTesting(new[] { ConditionalDateSuffix() });

        // isNew=false: the stale-strip (obsolete affix after a conditioning flip) is
        // preserved - a non-applicable rule's affix is still removed from an existing name.
        NamingValidationEngine
            .ApplyNamingStandards("Column", "AbcDate", scapiObject: null, autoOnly: false, isNew: false)
            .Should().Be("Abc");
    }

    [Fact]
    public void New_column_still_gets_an_applicable_affix_applied()
    {
        // Guard only skips NON-applicable rules: an unconditional (always-applicable)
        // suffix rule still applies on a new column.
        var always = new NamingStandardRule
        {
            Id = 2001,
            ObjectType = "Column",
            PropertyCode = "Physical_Name",
            RuleType = NamingRuleKind.Suffix,
            Suffix = "_C",
            IsActive = true,
            AutoApply = true,
            ApplyOn = RuleApplyOn.Both,
        };
        NamingStandardService.Instance.SeedForTesting(new[] { always });

        NamingValidationEngine
            .ApplyNamingStandards("Column", "Abc", scapiObject: null, autoOnly: false, isNew: true)
            .Should().Be("Abc_C");
    }

    // An always-applicable Suffix='Date' rule (rule#1032 once its Date-type condition is met).
    private static NamingStandardRule ApplicableDateSuffix() =>
        new()
        {
            Id = 1032,
            ObjectType = "Column",
            PropertyCode = "Physical_Name",
            RuleType = NamingRuleKind.Suffix,
            Suffix = "Date",
            IsActive = true,
            AutoApply = true,
            ApplyOn = RuleApplyOn.Both,
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Applicable_suffix_does_not_double_strip_a_name_already_ending_in_the_affix(bool isNew)
    {
        // Live repro: create column 'UpdateDate', set datatype Date -> Suffix='Date' rule#1032
        // applicable. 'UpdateDate' already ends with 'Date', so it must stay 'UpdateDate'. The
        // buggy while-loop stripped 'Date' TWICE (EndsWith is case-insensitive: 'Update' ends
        // with 'date') down to 'Up', then the re-apply appended 'Date' -> 'UpDate'. Not
        // isNew-specific - the double-strip hit applicable rules regardless.
        NamingStandardService.Instance.SeedForTesting(new[] { ApplicableDateSuffix() });

        NamingValidationEngine
            .ApplyNamingStandards("Column", "UpdateDate", scapiObject: null, autoOnly: false, isNew: isNew)
            .Should().Be("UpdateDate");
    }

    [Fact]
    public void Applicable_suffix_is_appended_when_missing()
    {
        // Sanity: the strip-once fix must not stop a genuinely-missing affix from being added.
        NamingStandardService.Instance.SeedForTesting(new[] { ApplicableDateSuffix() });

        NamingValidationEngine
            .ApplyNamingStandards("Column", "Update", scapiObject: null, autoOnly: false, isNew: true)
            .Should().Be("UpdateDate");
    }
}
