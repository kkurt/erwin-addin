using System.Reflection;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Coverage for the object-type-only Required rule (Property "(none)" ->
/// "an object of this type must exist", 2026-06-15). Two pure-logic seams are
/// testable without a DB or SCAPI: which rules the loader surfaces as existence
/// rules (<see cref="NamingStandardService.GetObjectExistenceRules"/>), and the
/// admin-OBJECT_TYPE -> SCAPI-Collect-class map the model-open check uses
/// (TableTypeMonitorService.ScapiCollectTypeForExistence, private static -
/// exercised via reflection, same pattern as the view placeholder test).
/// </summary>
// Both this class and NamingStandardEngineTests seed the NamingStandardService.Instance
// SINGLETON via SeedForTesting. xUnit runs different classes in parallel, so without a
// shared collection one class's seed/clear races the other's reads (tests pass isolated,
// flake in the full run). Same collection = serialized.
[Collection("NamingStandardSingleton")]
public class ExistenceRuleTests
{
    private static NamingStandardRule ExistenceRule(string objectType, string error = "must exist") =>
        new()
        {
            Id = 100,
            ObjectType = objectType,
            PropertyCode = "",                 // the object-type-only signal
            PropertyDefId = null,
            RuleType = NamingRuleKind.Required,
            IsRequired = true,
            ErrorMessage = error,
            IsActive = true,
        };

    private static NamingStandardRule PropertyRequiredRule(string objectType, string propertyCode) =>
        new()
        {
            Id = 200,
            ObjectType = objectType,
            PropertyCode = propertyCode,
            PropertyDefId = 53,
            RuleType = NamingRuleKind.Required,
            IsRequired = true,
            IsActive = true,
        };

    // ---------- GetObjectExistenceRules selection ----------

    [Fact]
    public void Existence_rules_are_surfaced_only_for_propertyless_Required()
    {
        NamingStandardService.Instance.SeedForTesting(new[]
        {
            ExistenceRule("TABLE"),
            ExistenceRule("VIEW"),
            PropertyRequiredRule("TABLE", "Physical_Name"),       // Required WITH a property
            new NamingStandardRule                                // Suffix - never an existence rule
            {
                Id = 300, ObjectType = "VIEW", PropertyCode = "Name",
                RuleType = NamingRuleKind.Suffix, Suffix = "_V", IsActive = true,
            },
        });
        try
        {
            var existence = NamingStandardService.Instance.GetObjectExistenceRules();

            existence.Should().HaveCount(2);
            existence.Select(r => r.ObjectType).Should().BeEquivalentTo(new[] { "TABLE", "VIEW" });
            existence.Should().OnlyContain(r => r.RuleType == NamingRuleKind.Required
                                                && string.IsNullOrEmpty(r.PropertyCode));
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    [Fact]
    public void Existence_rules_do_not_leak_into_the_per_property_paths()
    {
        NamingStandardService.Instance.SeedForTesting(new[]
        {
            ExistenceRule("TABLE"),
            PropertyRequiredRule("TABLE", "Physical_Name"),
        });
        try
        {
            // The property-keyed engine lookup must never return an existence
            // rule (empty PropertyCode is rejected outright).
            NamingStandardService.Instance.GetByObjectTypeAndProperty("TABLE", "").Should().BeEmpty();

            // GetRequiredPropertyCodes surfaces the real property, not the "" existence one.
            NamingStandardService.Instance.GetRequiredPropertyCodes("TABLE")
                .Should().BeEquivalentTo(new[] { "Physical_Name" });

            // GetPropertyCodes likewise excludes the empty existence code.
            NamingStandardService.Instance.GetPropertyCodes("TABLE")
                .Should().BeEquivalentTo(new[] { "Physical_Name" });
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    [Fact]
    public void Inactive_existence_rule_is_not_surfaced()
    {
        var inactive = ExistenceRule("TABLE");
        inactive.IsActive = false;
        NamingStandardService.Instance.SeedForTesting(new[] { inactive });
        try
        {
            NamingStandardService.Instance.GetObjectExistenceRules().Should().BeEmpty();
        }
        finally { NamingStandardService.Instance.SeedForTesting(System.Array.Empty<NamingStandardRule>()); }
    }

    // ---------- ScapiCollectTypeForExistence map ----------

    private static readonly MethodInfo? MapMethod =
        typeof(TableTypeMonitorService).GetMethod(
            "ScapiCollectTypeForExistence", BindingFlags.NonPublic | BindingFlags.Static);

    private static string? Map(string? objectType)
    {
        MapMethod.Should().NotBeNull(
            "ScapiCollectTypeForExistence must exist on TableTypeMonitorService (renamed?)");
        return (string?)MapMethod!.Invoke(null, new object?[] { objectType });
    }

    [Theory]
    [InlineData("TABLE", "Entity")]
    [InlineData("VIEW", "View")]
    [InlineData("COLUMN", "Attribute")]
    [InlineData("INDEX", "Key_Group")]
    [InlineData("SUBJECT AREA", "Subject_Area")]   // space form
    [InlineData("SUBJECT_AREA", "Subject_Area")]   // underscore form
    [InlineData("table", "Entity")]                // case-insensitive
    [InlineData("  View  ", "View")]               // trimmed
    public void Mappable_object_types_resolve_to_scapi_classes(string objectType, string expected)
    {
        Map(objectType).Should().Be(expected);
    }

    [Theory]
    [InlineData("MODEL")]      // the root itself - always exists, no check
    [InlineData("DOMAIN")]     // unmapped
    [InlineData("")]
    [InlineData(null)]
    public void Non_existence_object_types_map_to_null(string? objectType)
    {
        Map(objectType).Should().BeNull();
    }
}
